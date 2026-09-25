using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;
using UnityEngine;

/// <summary>
/// 因为是控制台, 不应该输入大量代码, 输入大量代码时, 推荐关闭高亮
/// </summary>
public class CodeHighLighter {

    private const string KeywordColor = "#579dd6ff";
    private const string TypeColor = "#4EC9B0";
    private const string MethodColor = "#DCDCAA";
    private const string VariableColor = "#9CDCFE";
    private const string ParameterColor = "#9CDCFE";
    private const string PropertyColor = "#9CDCFE";
    private const string FieldColor = "#9CDCFE";
    private const string StringColor = "#CE9178";
    private const string NumberColor = "#B5CEA8";
    private const string CommentColor = "#6A9955";
    private const string PreprocessorColor = "#C586C0";
    private const string NamespaceColor = "#C8C8C8";
    private const string EnumMemberColor = "#B5CEA8";
    
    public static void TestTree(string code) {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(kind:SourceCodeKind.Script));
        SyntaxNode root = tree.GetRoot();
        foreach(var token in root.DescendantTokens()) {
            Debug.Log($"{token.Kind()} | '{token.Text}' | Parent: {token.Parent?.Kind()}");
        }
    }
    /// <summary>
    /// 输入源码, 输出经过高亮的富文本
    /// </summary>
    /// <param name="code"></param>
    /// <param name="submittedScript"></param>
    /// <returns></returns>
    public string Highlight(string code, CodeExec codeExec) {
        if(string.IsNullOrEmpty(code)) return "";

        Script<object> current;
        if(codeExec.Script == null)
            current = CSharpScript.Create<object>(code, codeExec.ScriptOpts);
        else
            current = codeExec.Script.ContinueWith<object>(code);

        // Roslyn分析完一段代码后得到的"待编译程序"
        // 用里面的信息可以得到源码里每一个token是什么(变量名、类名、函数、关键字等等)
        Compilation compilation = current.GetCompilation();

        // 语法分析
        SyntaxTree tree = compilation.SyntaxTrees.First();
        SyntaxNode root = tree.GetRoot();

        // 语义分析
        SemanticModel semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility:true);
        StringBuilder builder = new(code.Length * 2);

        // 这里是遍历所有token
        foreach(SyntaxToken token in root.DescendantTokens()) {
            // 空格、换行、注释等;
            AppendTrivia(builder, token.LeadingTrivia);

            if (!token.IsKind(SyntaxKind.EndOfFileToken)) {
                string color = GetTokenColor(token, semanticModel);
                AppendText(builder, token.Text, color);
            }

            AppendTrivia(builder, token.TrailingTrivia);
        }

        return builder.ToString();
    }

    private string GetTokenColor(SyntaxToken token, SemanticModel semanticModel) {
        SyntaxKind kind = token.Kind(); // 基础颜色

        // 普通关键字
        // 因为SyntaxKind有几百个枚举, 不可能一个一个列举, 所以有这些Is函数
        if(SyntaxFacts.IsKeywordKind(kind))
            return KeywordColor;
        
        // var / async / await / partial 等 contextual keyword
        // 因为有些关键字只有处于特定的位置才是关键字
        // 例如partial可以当变量名, async和await在没有async和await的老版本C#中也可以当变量名
        // @var 这种转义标识符不能当关键字
        if(kind == SyntaxKind.IdentifierToken
        && !token.Text.StartsWith("@")
        && SyntaxFacts.GetContextualKeywordKind(token.ValueText) != SyntaxKind.None)
            return KeywordColor;
        
        SyntaxNode node = token.Parent;

        // 字符串、字符
        if(node is LiteralExpressionSyntax literal) { // 判断上下文
            if(literal.IsKind(SyntaxKind.StringLiteralExpression)
            || literal.IsKind(SyntaxKind.CharacterLiteralExpression))
                return StringColor;
            
            if(literal.IsKind(SyntaxKind.NumericLiteralExpression))
                return NumberColor;
        }

        // 插值字符串中的普通文本
        if(node is InterpolatedStringTextSyntax)
            return StringColor;

         // $"..." 中的 $" 和最后的 "
        if(node is InterpolatedStringExpressionSyntax interpolated) {
            if(token == interpolated.StringStartToken ||
               token == interpolated.StringEndToken)
                return StringColor;
        }

        // 不是标识符，剩下的 () {} + - = 等保持默认颜色
        if(kind != SyntaxKind.IdentifierToken)
            return null;
        
         // 标识符进入语义分析
        ISymbol symbol = GetSymbol(token, semanticModel);

        return GetSymbolColor(symbol);
    }

    // 类型、方法、属性、字段、局部变量、参数等等, 它们都是符号, ISymbol是它们统一的抽象
    private ISymbol GetSymbol(SyntaxToken token, SemanticModel semanticModel) {
        SyntaxNode node = token.Parent;

        if(node == null) return null;

        // 先找声明, 例如
        // class Player, void Test(), int value
        SyntaxNode current = node;
        while(current != null) {
            ISymbol declaredSymbol = semanticModel.GetDeclaredSymbol(current);

            if(declaredSymbol != null && IsSymbolDeclaredAtToken(declaredSymbol, token))
                return declaredSymbol;
            
            current = current.Parent;
        }

        // 找不到声明说明它是引用, 再找引用, 例如
        // GameObject.Find(), player.transform
        if(node is NameSyntax nameSyntax) {
            // 别名
            IAliasSymbol alias = semanticModel.GetAliasInfo(nameSyntax);

            if(alias != null) return alias;

        }
        SymbolInfo symbolInfo = semanticModel.GetSymbolInfo(node);

        if(symbolInfo.Symbol != null)
            return symbolInfo.Symbol;
        
        if(symbolInfo.CandidateSymbols.Length > 0)
            return symbolInfo.CandidateSymbols[0];
        
        return null;
    }

    private string GetSymbolColor(ISymbol symbol) {
        if(symbol == null) return null;

        if(symbol is IAliasSymbol alias) return GetSymbolColor(alias.Target);
        
        if(symbol is INamedTypeSymbol) return TypeColor;
        
        if(symbol is IMethodSymbol) return MethodColor;
        
        if(symbol is IPropertySymbol) return PropertyColor;

        if(symbol is IFieldSymbol field) {
            if(field.ContainingType?.TypeKind == TypeKind.Enum)
                return EnumMemberColor;
            return FieldColor;
        }

        if(symbol is ILocalSymbol) return VariableColor;

        if(symbol is IParameterSymbol) return ParameterColor;

        if(symbol is INamespaceSymbol) return NamespaceColor;

        if(symbol is ITypeParameterSymbol) return TypeColor;

        if(symbol is IEventSymbol) return FieldColor;

        return null;
    }

    private bool IsSymbolDeclaredAtToken(ISymbol symbol, SyntaxToken token) {
        foreach(Location loc in symbol.Locations) {
            if(!loc.IsInSource) continue;

            if(loc.SourceSpan.Contains(token.SpanStart))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 添加空格、换行、注释等
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="triviaList"></param>
    private void AppendTrivia(StringBuilder builder, SyntaxTriviaList triviaList) {
        foreach(SyntaxTrivia trivia in triviaList) {
            string color = null;
            switch(trivia.Kind()) {
                case SyntaxKind.SingleLineCommentTrivia:
                case SyntaxKind.MultiLineCommentTrivia:
                case SyntaxKind.SingleLineDocumentationCommentTrivia:
                case SyntaxKind.MultiLineDocumentationCommentTrivia:
                case SyntaxKind.DocumentationCommentExteriorTrivia:
                    color = CommentColor;
                    break;
            }

            if(trivia.HasStructure && trivia.GetStructure() is DirectiveTriviaSyntax)
                color = PreprocessorColor;
            AppendText(builder, trivia.ToFullString(), color);
        }
    }

    /// <summary>
    /// 添加一段指定颜色的文本
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="text"></param>
    /// <param name="color"></param>
    private void AppendText(StringBuilder builder, string text, string color) {
        if(string.IsNullOrEmpty(text)) return;
        if(color != null)
            builder.Append($"<color={color}>");
        AppendNoParseText(builder, text);
        if(color != null)
            builder.Append("</color>");
    }

    /// <summary>
    /// 按照'&lt;'分段, 防止代码里的'&lt;'被当成标签来解析
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="text"></param>
    private static void AppendNoParseText(StringBuilder builder, string text) {
        int start = 0;
        for(int k = 0; k < text.Length; ++k) {
            if(text[k] != '<') continue;

            if(k > start) {
                builder.Append("<noparse>");
                builder.Append(text, start, k - start);
                builder.Append("</noparse>");
            }

            builder.Append("<noparse><</noparse>");

            start = k + 1;
        }
        if(start < text.Length) {
            builder.Append("<noparse>");
            builder.Append(text, start, text.Length - start);
            builder.Append("</noparse>");
        }
    }
}
