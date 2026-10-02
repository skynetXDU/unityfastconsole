using System;
using System.Collections.Generic;
using System.Composition.Hosting;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using UnityEngine.UIElements;

namespace SKYNET {
public class CompletionApplyResult {

    public readonly string code;

    public readonly int cursorIndex;

    public CompletionApplyResult(string c, int index) {
        code = c; cursorIndex = index;
    }
}

public class CompletionDisplayItem {
    public readonly VectorImage icon;
    public readonly string text;

    public CompletionDisplayItem(VectorImage vi, string t) {
        icon = vi;
        text = t;
    }
}

public class Completion : IDisposable {

    private AdhocWorkspace workspace;

    private MetadataReference[] references;

    // 最后一个已经成功执行的submission
    private ProjectId committedProjectId;

    // 当前正在编辑、但还没有执行的submission
    private ProjectId currentProjectId;

    private DocumentId currentDocumentId;

    private int submissionIndex;

    public Document CurrentDocument => currentDocumentId == null ? null : workspace.CurrentSolution.GetDocument(currentDocumentId);

    public Completion() {
        Init();
    }

    private MefHostServices CreateMefHost() {
        List<Type> parts = new();

        foreach(Assembly assembly in MefHostServices.DefaultAssemblies) {
            try {
                parts.AddRange(assembly.GetTypes());
            }catch(ReflectionTypeLoadException e) {
                foreach(Type type in e.Types)
                    if(type != null) parts.Add(type);
            }
        }

        var container = new ContainerConfiguration().WithParts(parts).CreateContainer();
        return MefHostServices.Create(container);
    }

    private void Init() {
        references = CreateReferences();
        // workspace = new AdhocWorkspace(MefHostServices.DefaultHost);
        // 默认的host会因为无法加载sqlite而失败, 我们不需要sqlite, 也没有这个依赖, 因此自己生成一个host, 
        workspace = new AdhocWorkspace(CreateMefHost());
        submissionIndex = 0;
        currentProjectId = null;
        CreateCurrentSubmission("");
    }

    private MetadataReference[] CreateReferences() {
        List<MetadataReference> result = new();

        string[] assemblyPaths = AppDomain.CurrentDomain.GetAssemblies()
                                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                                .Select(a => a.Location)
                                .Distinct()
                                .ToArray();
        foreach(string path in assemblyPaths) {
            try {
                result.Add(MetadataReference.CreateFromFile(path));
            }catch{}
        }
        return result.ToArray();
    }


    // 创建一个新的submission, 供用户编辑
    private void CreateCurrentSubmission(string code) {
        ProjectId projectId = ProjectId.CreateNewId();
        DocumentId documentId = DocumentId.CreateNewId(projectId);
        // 以脚本模式解析
        CSharpParseOptions parseOptions = new(kind: SourceCodeKind.Script);
        CSharpCompilationOptions compilationOptions = new(OutputKind.DynamicallyLinkedLibrary);
        compilationOptions = compilationOptions.WithUsings("System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks", "UnityEngine", "UnityEditor");

        ProjectInfo projectInfo = ProjectInfo.Create(projectId, VersionStamp.Create(), $"submission_{submissionIndex}", $"submission_{submissionIndex}", LanguageNames.CSharp, isSubmission:true)
                                 .WithParseOptions(parseOptions)
                                 .WithCompilationOptions(compilationOptions)
                                 .WithMetadataReferences(references);
        // 如果存在已经成功提交的submission, 把它接在后面
        if(committedProjectId != null)
            projectInfo = projectInfo.WithProjectReferences(new[]{new ProjectReference(committedProjectId)});
        
        Solution solution = workspace.CurrentSolution.AddProject(projectInfo);
        TextLoader textLoader = TextLoader.From(TextAndVersion.Create(SourceText.From(code), VersionStamp.Create()));
        // 必须显式声明为Script, 否则会默认变成Regular
        DocumentInfo documentInfo = DocumentInfo.Create(documentId, $"submission_{submissionIndex}.csx", loader: textLoader, sourceCodeKind: SourceCodeKind.Script);
        solution = solution.AddDocument(documentInfo);
        // 再显式保证一次, 防止document被创建成Regular
        solution = solution.WithDocumentSourceCodeKind(documentId, sourceCodeKind: SourceCodeKind.Script);

        if(!workspace.TryApplyChanges(solution))
            throw new InvalidOperationException("创建Roslyn Submission失败");
        
        currentProjectId = projectId;
        currentDocumentId = documentId;
    }

    /// <summary>
    /// 用户编辑TextField时调用
    /// </summary>
    /// <param name="code"></param>
    public void UpdateCode(string code) {
        if(currentDocumentId == null) return;

        Solution solution = workspace.CurrentSolution.WithDocumentText(currentDocumentId, SourceText.From(code));
        workspace.TryApplyChanges(solution);
    }

    /// <summary>
    /// 获取自动补全列表, 并根据用户已输入文本进行过滤
    /// </summary>
    /// <param name="cursorIndex"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<List<CompletionItem>> GetCompletionListAsync(int cursorIndex, CancellationToken cancellationToken = default) {
        Document doc = CurrentDocument;
        if(doc == null) return null;

        CompletionService compSv = CompletionService.GetService(doc);
        if(compSv == null) return null;

        SourceText text = await doc.GetTextAsync(cancellationToken);
        cursorIndex = Math.Clamp(cursorIndex, 0, text.Length);

        CompletionList completionList = await compSv.GetCompletionsAsync(doc, cursorIndex, cancellationToken: cancellationToken);
        if(completionList == null) return new();

        TextSpan span = completionList.Span; // 这一次源码补全正作用于哪一段文字
        int length = Math.Max(0, cursorIndex - span.Start);

        string filterText = length > 0 ? text.ToString(new TextSpan(span.Start, length)) : "";
        List<CompletionItem> filteredItems = completionList.ItemsList
                                            .Where(item => item.FilterText.StartsWith(filterText, StringComparison.OrdinalIgnoreCase))
                                            .ToList();
        return filteredItems;//
    }
    
    /// <summary>
    /// 用户选择一个补全项之后, 让Roslyn计算替换哪部分文字
    /// </summary>
    /// <param name="item"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<CompletionApplyResult> ApplyCompletionAsync(CompletionItem item, CancellationToken cancellationToken = default) {
        Document doc = CurrentDocument;
        if(doc == null) return null;

        CompletionService compSv = CompletionService.GetService(doc);
        if(compSv == null) return null;

        CompletionChange change = await compSv.GetChangeAsync(doc, item, cancellationToken: cancellationToken);

        SourceText oldText = await doc.GetTextAsync(cancellationToken);
        SourceText newText = oldText.WithChanges(change.TextChange);

        string code = newText.ToString();

        UpdateCode(code);
        int cursorIndex = change.NewPosition ?? change.TextChange.Span.Start + change.TextChange.NewText.Length;

        return new CompletionApplyResult(code, cursorIndex);
    }

    /// <summary>
    /// 提交刚刚执行成功的代码
    /// </summary>
    /// <param name="code"></param>
    public void CommitSubmission(string code) {
        // 保证 Workspace 中保存的是刚刚成功执行的代码
        UpdateCode(code);

        committedProjectId = currentProjectId;
        
        ++submissionIndex;

        CreateCurrentSubmission("");
    }

    // Reset State 时一起调用
    public void ResetState() {
        workspace?.Dispose();

        workspace = null;
        committedProjectId = null;
        currentProjectId = null;
        currentDocumentId = null;

        Init();
    }

    public void Dispose() {
        workspace?.Dispose();
        workspace = null;
    }
}
}