using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Scripting;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// 一段用户输入的C#脚本要执行, 首先要编译成dll, 才能被加载执行
public class CodeExec {

    // 一段C#脚本的描述以及它和前面脚本之间的关系
    private Script<object> script;

    public Script<object> Script => script;

    private ScriptOptions scriptOptions;

    public ScriptOptions ScriptOpts {
        get {
            if(scriptOptions != null) return scriptOptions;

            // 防止直接AddReferences(assembly)锁文件导致整个项目重编译失败
            var references = AppDomain.CurrentDomain.GetAssemblies()
                         .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                         .Select(a => MetadataReference.CreateFromFile(a.Location));

            ScriptOptions options = ScriptOptions.Default
                                    .AddReferences(references)
                                    .AddImports(
                                        "System",
                                        "System.Collections.Generic",
                                        "System.Linq",
                                        "System.Threading.Tasks",
                                        "UnityEngine",
                                        "UnityEditor"
                                    );
            return scriptOptions = options;
        }
    }

    // submission表示一次脚本提交、一次控制台执行, 后面的submission可以访问前面submission的变量
    // Roslyn为每一次submission编译出一个独立的、隐藏的程序集, 程序集里面通常有一个隐藏类型
    // 第 0 个槽固定为 globals，这里没有 globals，所以为 null
    private object[] submissionStates = new object[2];

    // 有效槽数量，初始只有 globals
    private int submissionStateCount = 1;

    private bool isExecuting;

    public async Task<bool> ExecuteCode(string code) {
        if(string.IsNullOrEmpty(code))
            return false;
        // 防止快速重复点击，同时运行两个 submission
        if(isExecuting)
            return false;

        isExecuting = true;

        try {
            // 先构建候选 Script，成功之前不修改正式状态
            // 这里不能简单 scriptState = await CSharpScript.RunAsync(code, GetScriptOptions());
            // 因为unity内部没有实现好加载dll的逻辑, 因此需要自己完成加载
            Script<object> newScript;
            if(script == null)
                newScript = CSharpScript.Create<object>(code, ScriptOpts);
            else
                newScript = script.ContinueWith<object>(code);

            // Roslyn分析完一段代码后得到的"待编译程序"
            // 里面包含了语法树、类型信息、引用程序集、入口函数、前一次submission
            // 但是还没有产生dll
            Compilation compilation = newScript.GetCompilation();

            // 复制已有状态，并为新的 submission 预留一个槽
            object[] newSubmissionStates = new object[Math.Max(2, submissionStateCount + 1)];
            Array.Copy(submissionStates, newSubmissionStates, submissionStateCount);

            object returnValue = await UnityScriptSubmissionExecutor.Execute(compilation,newSubmissionStates);

            // submission 如果包含变量声明等状态，
            // Roslyn 生成的构造函数会把自身写到这个槽里
            int newSubmissionStateCount = submissionStateCount;
            if(newSubmissionStates[submissionStateCount] != null)
                ++newSubmissionStateCount;

            // 编译、加载、执行全部成功以后才正式提交状态
            script = newScript;
            submissionStates = newSubmissionStates;
            submissionStateCount = newSubmissionStateCount;

            if(returnValue != null) Debug.Log(returnValue);
            return true;
        }catch(CompilationErrorException e) {
            foreach(Diagnostic diagnostic in e.Diagnostics)
                Debug.LogError(diagnostic.ToString());
            return false;
        }catch(Exception e) {
            Debug.LogException(e);
            return false;
        }finally {
            isExecuting = false;
        }
    }

    public void ResetState() {
        script = null;
        submissionStates = new object[2];
        submissionStateCount = 1;
    }
}

internal static class UnityScriptSubmissionExecutor {

    // Assembly.Load(byte[]) 加载出来的 submission 没有磁盘路径。
    // 后一个 submission 引用前一个 submission 时，用这里做兜底解析。
    private static readonly Dictionary<string, Assembly> loadedAssemblies = new();

    static UnityScriptSubmissionExecutor() {
        // 注册找缓存的函数
        AppDomain.CurrentDomain.AssemblyResolve += HandleAssemblyResolve;
    }

    // 自己实现的执行函数, 取代scriptState = await CSharpScript.RunAsync(code, GetScriptOptions());
    public static async Task<object> Execute(Compilation compilation, object[] submissionStates) {
        // 先检查语法、绑定等编译错误, 有错误就抛异常
        ImmutableArray<Diagnostic> diagnostics = ImmutableArray.CreateRange(
            compilation.GetDiagnostics()
                       .Where(d => d.Severity == DiagnosticSeverity.Error)
        );
        ThrowIfCompilationFailed(diagnostics);
        
        using MemoryStream peStream = new();
        // 把compilation真正编译成.NET程序集二进制, 并写入内存, 不落盘
        EmitResult emitResult = compilation.Emit(peStream);
        diagnostics = ImmutableArray.CreateRange(
            emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
        );
        ThrowIfCompilationFailed(diagnostics);

        // 拿到程序入口点;
        // 正常的C#程序有Main函数, 但Rosyln Script没有, 因此编译器会自动生成一个入口点
        // 这个入口点里保存着: 方法叫什么, 在哪个类型里, 返回类型是什么, 参数是什么
        // 但它不是入口函数本身
        IMethodSymbol entryPoint = compilation.GetEntryPoint(CancellationToken.None);

        // 编译失败、找不到入口点、没有生成dll
        if (!emitResult.Success || entryPoint == null || peStream.Length == 0)
            return null;

        // 编译完之后, peStream里面就装着生成的dll的内容
        // 这里转成字节数组
        byte[] assemblyBytes = peStream.ToArray();

        // 再从字节数组加载程序集
        // 关键：不再使用InteractiveAssemblyLoader.LoadAssemblyFromStream
        // 避开unity未实现的部分
        Assembly assembly = Assembly.Load(assemblyBytes);

        RegisterAssembly(assembly);

        // 根据入口点和程序集, 拿到真正的执行函数
        MethodInfo entryPointMethod = GetEntryPointMethod(entryPoint, assembly);

        // 把隐藏的执行函数转为能调用的C#委托
        var executor = (Func<object[], Task<object>>)
            entryPointMethod.CreateDelegate(
                typeof(Func<object[], Task<object>>)
            );

        // 调用这个委托, 就执行了用户输入的代码
        // 执行时, 会把自己塞进传入的object[]
        return await executor(submissionStates);
    }

    private static MethodInfo GetEntryPointMethod( IMethodSymbol entryPoint, Assembly assembly) {
        // entryPoint.ContainingType就是"这个方法属于哪个类"
        string typeName = GetMetadataTypeName(entryPoint.ContainingType);

        // 从加载的程序集里找到入口点所在的那个类
        Type entryPointType = assembly.GetType(typeName, true, false);

        // 再拿到隐藏的执行函数
        MethodInfo method = entryPointType
                            .GetMethods(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)
                            .FirstOrDefault(m =>
                                m.Name == entryPoint.MetadataName &&
                                m.GetParameters().Length == 1 &&
                                m.GetParameters()[0].ParameterType == typeof(object[])
                            )
                            ?? throw new MissingMethodException(typeName, entryPoint.MetadataName);
        return method;
    }

    // 把 Roslyn 的 INamedTypeSymbol 转成 Assembly.GetType() 能识别的"运行时类型全名"。
    private static string GetMetadataTypeName(INamedTypeSymbol type) {
        string typeName = type.MetadataName;

        // 理论上 Script Submission 是顶级类型，
        // 这里顺便兼容嵌套类型的 metadata 名称
        INamedTypeSymbol containingType = type.ContainingType;
        while(containingType != null) {
            typeName = $"{containingType.MetadataName}+{typeName}";
            containingType = containingType.ContainingType;
        }

        if(type.ContainingNamespace == null ||
           type.ContainingNamespace.IsGlobalNamespace)
            return typeName;

        return $"{type.ContainingNamespace.ToDisplayString()}.{typeName}";
    }

    private static void ThrowIfCompilationFailed(ImmutableArray<Diagnostic> diagnostics) {
        if(diagnostics.IsDefaultOrEmpty)
            return;
        throw new CompilationErrorException(diagnostics[0].ToString(), diagnostics);
    }

    // 把submission编译好的程序集保存起来
    // 因为是内存程序集, 没有磁盘路径
    // 后面的submission又会依赖前面的submission
    // 因此这里按照名字缓存起来
    private static void RegisterAssembly(Assembly assembly) {
        lock(loadedAssemblies) { // lock是同一时刻只允许一个线程访问
            loadedAssemblies[assembly.FullName] = assembly;
        }
    }

    private static Assembly HandleAssemblyResolve(object sender, ResolveEventArgs args) {
        lock(loadedAssemblies) {
            if(loadedAssemblies.TryGetValue(args.Name, out Assembly assembly))
                return assembly;
        }
        return null;
    }
}