using UnityEditor;
using UnityEngine.UIElements;
using UnityEngine;
using UnityEditor.UIElements;
using System;
using System.Text;
using Microsoft.CodeAnalysis.Completion;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using PlasticGui.WorkspaceWindow.Items;

public class ConsoleEditor : EditorWindow {

    private static class MathUtil {
        public static int LowerBound<T>(IList<T> a, T value) where T : IComparable<T> {
            int left = 0, right = a.Count;
            while (left < right) {
                int mid = left + (right - left) / 2;
                if (a[mid].CompareTo(value) < 0)
                    left = mid + 1;
                else
                    right = mid;
            }
            return left;
        }
        public static int UpperBound<T>(IList<T> a, T value) where T : IComparable<T> {
            int left = 0, right = a.Count;
            while (left < right) {
                int mid = left + (right - left) / 2;
                if (a[mid].CompareTo(value) <= 0)
                    left = mid + 1;
                else
                    right = mid;
            }
            return left;
        }
    }

    private const string FontPrefKey = "unity_fast_console_selected_font";
    private const string FontSizePrefKey = "unity_fast_console_selected_font_size";
    private const string LineSpeacingKey = "unity_fast_console_line_spacing";
    private const string HighlightEnabled = "unity_fast_console_highlight_enabled";
    private static readonly int[] FontSizes = {10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20};
    private static readonly int[] LineSpacings = {0, 12, 24, 36, 48, 60, 72, 84, 96, 100};

    [SerializeField]
    private VisualTreeAsset uxml;

    [SerializeField]
    private IconSets iconSets;

    private ToolbarButton executeButton;

    private ToolbarButton resetButton;

    private ToolbarMenu fontMenu;

    private ToolbarMenu fontSizeMenu;

    private ToolbarMenu lineSpacingMenu;

    private ToolbarToggle highlightEnabledToggle;

    private VisualElement editorCodeElement;

    private Label lineIndexLabel;

    private TextField codeInputField;

    private Label codeHighlightLabel;

    private ScrollView codeInputScroller;

    private ListView completionListView;

    private CodeExec codeExec;

    private CodeHighLighter codeHighLighter;

    private Completion completion;

    private CancellationTokenSource completionCts; // 用于取消已经开始的异步请求

    private List<CompletionItem> compList = new();

    private Vector2 preservedScrollOffset;

    private int preservedCursorIndex;

    private int preservedSelectIndex;

    private int lastFirstLine = -1; // 上一次更新行号时, 第一行的编号

    private int lastVisibleLines = -1; // 上一次更新行号时, 可见的行数

    [MenuItem("Tools/快速控制台2")]
    public static void OpenConsole() {
        GetWindow<ConsoleEditor>("C#控制台"); // 调用它打开窗口
    }

    public void CreateGUI() { // 打开窗口后的回调函数
        uxml.CloneTree(rootVisualElement);

        executeButton = rootVisualElement.Q<ToolbarButton>("execute_button");
        executeButton.clicked += HandleExecuteCode;

        resetButton = rootVisualElement.Q<ToolbarButton>("reset_button");
        resetButton.clicked += HandleResetState;

        editorCodeElement = rootVisualElement.Q<VisualElement>("editor_code");

        lineIndexLabel = rootVisualElement.Q<Label>("line_index");

        codeInputField = rootVisualElement.Q<TextField>("code_input");
        codeInputScroller = codeInputField.Q<ScrollView>();
        // 注册失焦时滚动位置的保护
        RegisterPreserveState(codeInputField, codeInputScroller);
        codeInputField.style.whiteSpace = WhiteSpace.Pre; // 保留连续空格、换行
        codeInputField.RegisterCallback<KeyDownEvent>(HandleEditKeyDown, TrickleDown.TrickleDown);
        // 布局发生变化时算行高、更新行号
        codeInputField.RegisterCallback<GeometryChangedEvent>(evt => { HandleScroll(); });
        // 滚动时更新行号
        codeInputScroller.verticalScroller.valueChanged += y => { HandleScroll(); };
        // 编辑时重新计算高亮
        codeInputField.RegisterValueChangedCallback(HandleCodeEdited);
        // 移动光标时让候选框始终追随
        codeInputField.RegisterCallback<KeyDownEvent>(HandleCursorMove, CallbackOptions.TrickleDown);

        codeHighlightLabel = rootVisualElement.Q<Label>("code_highlight");
        codeHighlightLabel.style.whiteSpace = WhiteSpace.Pre; // 保留连续空格、保留换行

        completionListView = rootVisualElement.Q<ListView>("completion_list");
        completionListView.itemsSource = compList;
        completionListView.bindItem = BindCompletionItem;
        completionListView.style.display = DisplayStyle.None;
        // 点击任意位置关闭候选框
        codeInputField.RegisterCallback<PointerDownEvent>(HandleHideCompletionList, TrickleDown.TrickleDown);

        fontMenu = rootVisualElement.Q<ToolbarMenu>("font_menu");
        RegisterFont(fontMenu);

        fontSizeMenu = rootVisualElement.Q<ToolbarMenu>("font_size_menu");
        RegisterFontSize(fontSizeMenu);

        lineSpacingMenu = rootVisualElement.Q<ToolbarMenu>("line_spacing_menu");
        RegisterLineSpacing(lineSpacingMenu);

        highlightEnabledToggle = rootVisualElement.Q<ToolbarToggle>("highlight_enabled");
        RegisterCodeHighlight(highlightEnabledToggle);

        codeExec = new();
        codeHighLighter = new();
        completion = new();
    }

    void OnDisable() { // 关闭窗口时也取消已经开始的补全请求
        // 这里是通用的写法, CancellationToken允许注册取消回调
        // 如果取消回调里又发起了新的请求, 直接写completionCts?.Cancel();会把新的请求也取消掉;
        // 但是这个项目里一般没有, 这里只是用了通用的写法
        CancellationTokenSource cts = completionCts;
        completionCts = null;
        cts?.Cancel();
        completion?.Dispose();
        completion = null;
    }

    private void RegisterFont(ToolbarMenu menu) {
        menu.menu.ClearItems();

        // 拿到本机所有已安装字体
        string[] fontNames = Font.GetOSInstalledFontNames();
        Array.Sort(fontNames, StringComparer.OrdinalIgnoreCase);

        // 绑定字体选项
        foreach(string fontName in fontNames) {
            string fn = fontName;
            char firstLetter = char.ToUpperInvariant(fn[0]);

            menu.menu.AppendAction(
                $"{firstLetter}/{fn}",
                action => {
                    menu.userData = fn;
                    menu.text = $"字体: {fn}";
                    SetFont(fn);
                },
                action => {
                    return menu.userData as string == fn ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal;
                }
            );
        }
        // 读取上一次保存的选项
        string savedFont = EditorUserSettings.GetConfigValue(FontPrefKey);
        if(string.IsNullOrEmpty(savedFont) || !Array.Exists(fontNames, f => f == savedFont))
            savedFont = fontNames.Length > 0 ? fontNames[0] : "";
        menu.userData = savedFont;
        menu.text = $"字体: {savedFont}";
        SetFont(savedFont);
    }

    private void RegisterFontSize(ToolbarMenu menu) {
        menu.menu.ClearItems();

        // 绑定字号选项
        foreach(int fontSize in FontSizes) {
            int fs = fontSize;
            menu.menu.AppendAction(
                $"{fs}",
                action => {
                    menu.userData = fs;
                    menu.text = $"字号: {fs}";
                    SetFontSize(fs);
                },
                action => {
                    return menu.userData as int? == fs ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal;
                }
            );
        }

        // 读取上一次保存的选项
        string str = EditorUserSettings.GetConfigValue(FontSizePrefKey);
        int savedFontSize = 12;
        if(!string.IsNullOrEmpty(str) && int.TryParse(str, out int parsedFontSize))
            savedFontSize = parsedFontSize;
        menu.userData = savedFontSize;
        menu.text = $"字号: {savedFontSize}";
        SetFontSize(savedFontSize);
    }

    private void RegisterLineSpacing(ToolbarMenu menu) {
        menu.menu.ClearItems();

        // 绑定字号选项
        foreach(int lineSpacing in LineSpacings) {
            int ls = lineSpacing;
            menu.menu.AppendAction(
                $"{ls}px",
                action => {
                    menu.userData = ls;
                    menu.text = $"行距: {ls}px";
                    SetLineSpacing(ls);
                },
                action => {
                    return menu.userData as int? == ls ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal;
                }
            );
        }

        // 读取上一次保存的选项
        string str = EditorUserSettings.GetConfigValue(LineSpeacingKey);
        int savedLineSpacing = 0;
        if(!string.IsNullOrEmpty(str) && int.TryParse(str, out int parsedLineSpacing))
            savedLineSpacing = parsedLineSpacing;
        menu.userData = savedLineSpacing;
        menu.text = $"行距: {savedLineSpacing}px";
        SetLineSpacing(savedLineSpacing);
    }

    private void RegisterCodeHighlight(ToolbarToggle toggle) {
        string str = EditorUserSettings.GetConfigValue(HighlightEnabled);
        bool highlightEnabled;
        if(string.IsNullOrEmpty(str))
            highlightEnabled = true;
        else {
            if(str == "0") highlightEnabled = false;
            else highlightEnabled = true;
        }
        toggle.SetValueWithoutNotify(highlightEnabled);
        toggle.RegisterValueChangedCallback(HandleHighlightEnabled);
        codeHighlightLabel.enableRichText = highlightEnabled;
    }
    
    // TextField失去焦点时, blur事件会让TextField跳回顶部
    // 因此这里保护一下相关状态, 包括滚动位置、光标位置、选择项
    // Blur和FocusOutEvent的联系: Blur触发于失去焦点后, FocusOutEvent触发于失去焦点前, Blur时焦点已经不在了, FocusOutEvent时焦点还在
    // Focus和FocusInEvent类似: Focus触发于获得焦点之后, FocusInEvent触发于获得焦点之前;
    // 而且Blur/Focus不会冒泡到父元素, FocusInEvent/FocusOutEvent会冒泡到父元素
    private void RegisterPreserveState(TextField textField, ScrollView scrollView) {
        textField.RegisterCallback<FocusOutEvent>(evt => {
            preservedScrollOffset = scrollView.scrollOffset;
            preservedCursorIndex = textField.cursorIndex;
            preservedSelectIndex = textField.selectIndex;
        });

        textField.RegisterCallback<BlurEvent>(evt => {
            textField.schedule.Execute(() => {
                textField.Focus();
                textField.SelectRange(preservedCursorIndex, preservedSelectIndex);
                scrollView.scrollOffset = preservedScrollOffset;
                HandleScroll();
            });
        });
    }

    private void SetFont(string fontName) {
        EditorUserSettings.SetConfigValue(FontPrefKey, fontName);
        Font font = Font.CreateDynamicFontFromOSFont(fontName, 14);
        FontDefinition fontDef = FontDefinition.FromFont(font);
        if(font != null){
            lineIndexLabel.style.unityFontDefinition = fontDef;
            codeInputField.style.unityFontDefinition = fontDef;
            completionListView.style.unityFontDefinition = fontDef;
        }
    }

    private void SetFontSize(int fontSize) {
        EditorUserSettings.SetConfigValue(FontSizePrefKey, $"{fontSize}");
        lineIndexLabel.style.fontSize = fontSize;
        codeInputField.style.fontSize = fontSize;
    }

    private void SetLineSpacing(int lineSpacing) {
        EditorUserSettings.SetConfigValue(LineSpeacingKey, $"{lineSpacing}");
        lineIndexLabel.style.unityParagraphSpacing = lineSpacing;
        codeInputField.style.unityParagraphSpacing = lineSpacing;
    }

    private void UpdateCompletionListPosition() {
        if(completionListView.resolvedStyle.display == DisplayStyle.None)
            return;
        Vector2 posInRoot = codeInputField.ChangeCoordinatesTo(editorCodeElement, codeInputField.cursorPosition);
        completionListView.style.position = Position.Absolute;
        completionListView.style.left = posInRoot.x;
        completionListView.style.top = posInRoot.y;
    }

    private void HandleScroll() {
        IResolvedStyle style = codeInputField.resolvedStyle;
        // 计算可见区域的高度
        float height = style.height - style.paddingTop - style.paddingBottom - style.borderTopWidth - style.borderBottomWidth; 
        // 计算行高
        float lineHeight = style.fontSize * (1.0f + (int)lineSpacingMenu.userData * 0.01f);
        // 可见区域的行数
        int visibleLines = (int)Math.Ceiling(height / lineHeight);
        // 第一行的编号
        int firstLine = (int)Math.Floor(codeInputScroller.scrollOffset.y / lineHeight);
        float offsetY = -(codeInputScroller.scrollOffset.y % lineHeight);

        // 构建行号
        // 只有第一行发生变化或可见行数发生变化时, 才更新行号
        if(firstLine != lastFirstLine || visibleLines != lastVisibleLines){
            StringBuilder lineIndicesBuilder = new();
            for(int index = 1; index <= visibleLines; ++index){
                int lineIndex = firstLine + index;
                lineIndicesBuilder.AppendLine(lineIndex.ToString());
            }
            lineIndexLabel.text = lineIndicesBuilder.ToString();
        }
        lineIndexLabel.style.translate = new Translate(0, offsetY);
        codeHighlightLabel.style.translate = new Translate(0, -codeInputScroller.scrollOffset.y);
        lastFirstLine = firstLine;
        lastVisibleLines = visibleLines;
    }

    private async void HandleExecuteCode() {
        bool success = await codeExec.ExecuteCode(codeInputField.text);
        if(success)
            completion.CommitSubmission(codeInputField.text);
    }

    private void HandleResetState() {
        codeExec?.ResetState();
    }

    private void BindCompletionItem(VisualElement element, int index) {
        element.Q<Image>("icon").vectorImage = iconSets.GetIconByCompletionItem(compList[index]);
        element.Q<Label>("text").text = compList[index].DisplayText;
    }

    private void HandleHideCompletionList(PointerDownEvent evt = null) {
        // 让异步请求失效
        CancellationTokenSource cts = completionCts;
        completionCts = null;
        cts?.Cancel();
        completionListView.style.display = DisplayStyle.None;
    }

    private void UpdateCodeLabel(string newValue) {
        if(highlightEnabledToggle.value){
            string highlightText = codeHighLighter.Highlight(newValue, codeExec);
            codeHighlightLabel.text = highlightText;
        }
        else
            codeHighlightLabel.text = newValue;
        
        completion.UpdateCode(newValue);
    }

    // 编辑代码时调用, 执行高亮、自动补全
    private async void HandleCodeEdited(ChangeEvent<string> changeEvent) {
        if(changeEvent.target != codeInputField) return;
        
        UpdateCodeLabel(changeEvent.newValue);

        int prevLen = changeEvent.previousValue != null ? changeEvent.previousValue.Length : 0;
        int newLen = changeEvent.newValue != null ? changeEvent.newValue.Length : 0;
        if(newLen < prevLen) { // 如果是退格, 就不启动补全
            HandleHideCompletionList();
            return;
        }

        completionCts?.Cancel(); // 取消上一次
        CancellationTokenSource currentCts = new();
        completionCts = currentCts;
        try {
            await Task.Delay(100, currentCts.Token);

            compList = await completion.GetCompletionListAsync(codeInputField.cursorIndex, currentCts.Token);
            compList ??= new();
            // 因为CancellationToken是协作式取消, cancel只是发一个取消的信号, 补全任务却不一定立即停止, 仍然可能返回
            // 因此检测是否取消过以及是否为最新的补全请求
            if(currentCts.IsCancellationRequested || !ReferenceEquals(completionCts, currentCts))
                return;
        }catch(OperationCanceledException){}
        finally { // 无论代码是正常完成、发生异常, 还是中途return, finally都会执行
            if(ReferenceEquals(completionCts, currentCts)) // 判断当前请求是不是最新的
                completionCts = null;
            currentCts.Dispose(); // 这是在释放资源
        }
        if(compList.Count <= 0){
            HandleHideCompletionList();
            return;
        }
        completionListView.style.display = DisplayStyle.Flex;
        completionListView.itemsSource = compList;
        completionListView.RefreshItems();
        
        codeInputField.schedule.Execute(() => {
            completionListView.SetSelection(0);
            completionListView.ScrollToItem(0);
            UpdateCompletionListPosition();
        });
    }

    private void HandleEditKeyDown(KeyDownEvent evt) {
        if(completionListView.resolvedStyle.display == DisplayStyle.None)
            InputCode(evt);
        else
            SelectAndApplyCompletionByKeyDown(evt);
    }
    private void InputCode(KeyDownEvent evt) {

        if(evt.keyCode == KeyCode.Tab) {
            // 上接(1):
            // 为什么Tab不用处理?
            // 因为unity内部针对tab的第二次事件作了处理, 默认行为不会把第二次'\t'字符插入文本
            // Enter没有这个处理, 因此需要手动拦截掉
            evt.StopPropagation();
            InputTab(evt.shiftKey);
        }
        else if(evt.character == '{' || evt.character == '[' || evt.character == '(' || evt.character == '\'' || evt.character == '\"') { // 自动补全括号
            evt.StopPropagation();
            InputSpecialChar(evt.character);
        }
    }
    private void InputTab(bool shiftKey) {
        string value = codeInputField.value ?? "";
        // 选区
        int start = Math.Min(codeInputField.cursorIndex, codeInputField.selectIndex);
        int end = Math.Max(codeInputField.cursorIndex, codeInputField.selectIndex);

        // 找出选区涉及的行
        int lineStart = start > 0 ? value.LastIndexOf('\n', start - 1) + 1 : 0;
        int lineEnd = value.IndexOf('\n', end);
        if(lineEnd < 0) lineEnd = value.Length - 1;
        lineEnd += 1;
        string[] lines = value[lineStart..lineEnd].Split('\n');
        int i = 0, j = lines.Length;
        while(i < j && lines[i] == "") ++i;
        while(j > i && lines[j - 1] == "") ++j;
        lines = lines[i..j];
        if(lines.Length == 0)
            lines = new[]{""};

        if(lines.Length == 1) { // 只选中了一行, 插入或开头减少4空格缩进
            string newValue;
            if(!shiftKey)
                newValue = value[..start] + "    " + value[end..];
            else {
                if(lines[0].StartsWith("    ")) lines[0] = lines[0][4..];
                else if(lines[0].StartsWith("   ")) lines[0] = lines[0][3..];
                else if(lines[0].StartsWith("  ")) lines[0] = lines[0][2..];
                else if(lines[0].StartsWith(" ")) lines[0] = lines[0][1..];
                newValue = value[..lineStart] + lines[0];
                if(lineEnd < value.Length)
                    newValue += '\n' + value[lineEnd..];
            }
            codeInputField.SetValueWithoutNotify(newValue);
            UpdateCodeLabel(newValue);
            codeInputField.SelectRange(codeInputField.cursorIndex + 4, codeInputField.selectIndex + 4);
        }
        else { // 选中了多行, 每行插入或开头减少4空格缩进
            int d = 0, d0 = 0;
            if(!shiftKey)
                for(i = 0; i < lines.Length; ++i)
                    lines[i] = "    " + lines[i];
            else
                for(i = 0; i < lines.Length; ++i){
                    if(lines[i].StartsWith("    ")) { lines[i] = lines[i][4..]; d += 4; }
                    else if(lines[i].StartsWith("   ")) { lines[i] = lines[i][3..]; d += 3; }
                    else if(lines[i].StartsWith("  ")) { lines[i] = lines[i][2..]; d += 2; }
                    else if(lines[i].StartsWith(" ")) { lines[i] = lines[i][1..]; d += 1; }
                    if(i == 0) d0 = d;
                }
            string newValue = value[..lineStart] + string.Join('\n', lines);
            if(lineEnd < value.Length)
                newValue += '\n' + value[lineEnd..];
            codeInputField.SetValueWithoutNotify(newValue);
            UpdateCodeLabel(newValue);
            if(codeInputField.cursorIndex > codeInputField.selectIndex) { // 从后往前选的
                if(!shiftKey)
                    codeInputField.SelectRange(end + lines.Length * 4, start + 4);
                else
                    codeInputField.SelectRange(end - d, start - d0);
            }
            else { // 从前往后选的
                if(!shiftKey)
                    codeInputField.SelectRange(start + 4, end + lines.Length * 4);
                else
                    codeInputField.SelectRange(start - d0, end - d);
            }
        }
    }

    private string GetCharPair(char c) {
        if(c == '(') return "()";
        if(c == '[') return "[]";
        if(c == '{') return "{}";
        if(c == '\"') return "\"\"";
        if(c == '\'') return "\'\'";
        return "";
    }

    private bool InPairChar(string value, int index) {
        int p = index - 1;
        while(p >= 0 && (value[p] == ' ' || value[p] == '\n' || value[p] == '\t' || value[p] == '\r'))
            --p;
        int q = index;
        while(q < value.Length && (value[q] == ' ' || value[q] == '\n' || value[q] == '\t' || value[q] == '\r'))
            --q;
        return p >= 0 && q < value.Length && IsPairChar(value[p], value[q]);
    }

    private bool IsPairChar(char c1, char c2) {
        if(c1 == '(' && c2 == ')') return true;
        if(c1 == '[' && c2 == ']') return true;
        if(c1 == '{' && c2 == '}') return true;
        if(c1 == '\'' && c2 == '\'') return true;
        if(c1 == '\"' && c2 == '\"') return true;
        return false;
    }

    private char PairChar(char c) {
        if(c == '(') return ')';
        if(c == '[') return ']';
        if(c == '{') return '}';
        if(c == '\"') return '\"';
        if(c == '\'') return '\'';
        return '\0';
    }

    // 输入特殊字符(各种括号、单双引号)
    // 如果没有选区
    //      如果光标处是不可见字符, 就插入自动补全的括号或引号
    //      如果后面有可见字符, 就按照默认行为插入
    // 如果出现选区, 就用根据输入的字符用括号或者单双引号把选区包起来
    private void InputSpecialChar(char c) {
        // 选区
        string value = codeInputField.value ?? "";
        int start = Math.Min(codeInputField.cursorIndex, codeInputField.selectIndex);
        int end = Math.Max(codeInputField.cursorIndex, codeInputField.selectIndex);
        
        if(start == end) { // 没有选区
            // 后面是不可见字符或在成对字符内部
            if(start == value.Length || value[start] == ' ' || value[start] == '\n' || value[start] == '\t' || value[start] == '\r' || InPairChar(value, start)) {
                string newValue = value[..start] + GetCharPair(c);
                if(end < value.Length) newValue += value[end..];
                codeInputField.SetValueWithoutNotify(newValue);
                UpdateCodeLabel(newValue);
                codeInputField.SelectRange(start + 1, start + 1);
            }
            else {
                string newValue = value[..start] + c;
                if(end < value.Length) newValue += value[end..];
                codeInputField.SetValueWithoutNotify(newValue);
                UpdateCodeLabel(newValue);
                codeInputField.SelectRange(start + 1, start + 1);
            }
        }
        else { // 有选区
            string newValue = value[..start] + c + value[start..end] + PairChar(c);
            if(end < value.Length) newValue += value[end..];
            codeInputField.SetValueWithoutNotify(newValue);
            UpdateCodeLabel(newValue);
            if(start < end) // 从前往后选的
                codeInputField.SelectRange(start + 1, end + 1);
            else // 从后往前选的
                codeInputField.SelectRange(end + 1, start + 1);
        }
    }

    // 上下箭头控制选择补全项以及按Tab应用补全项
    private void SelectAndApplyCompletionByKeyDown(KeyDownEvent evt) {
        if(compList == null || compList.Count <= 0) return;

        // 上下箭头选择补全项
        if(evt.keyCode == KeyCode.UpArrow || evt.keyCode == KeyCode.DownArrow) {
            int d = evt.keyCode == KeyCode.UpArrow ? -1 : 1;
            evt.StopPropagation();
            MoveCompletionSelection(d);
            return;
        }
        // 按Tab应用补全项
        if(evt.keyCode == KeyCode.Tab) {
            if(evt.shiftKey || evt.ctrlKey || evt.altKey || evt.commandKey)
                return;
            evt.StopPropagation(); // 立即停止事件传播, 防止HandleTab也被调用
            ApplySelectedCompletionAsync();
            return;
        }
        // 按回车应用补全项, 回车比较特殊, 见下文注释
        bool isReturnKey = evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter;
        bool isNewLineChar = evt.character == '\n' || evt.character == '\r';
        if(!isReturnKey && !isNewLineChar)
            return;
        if(evt.shiftKey || evt.ctrlKey || evt.altKey || evt.commandKey)
            return;
        // (1) unity会对产生字符的按键发送两次事件, 其中包括回车和Tab
        // 第一次表示物理按键事件, 携带KeyCode, character为0
        // 第二次表示字符, KeyCode为None, character为本次按键产生的字符
        // 因此针对回车产生的这两次事件, 都在这里吃掉
        // 但只有第二次带'\n'或'\r'的事件应用补全
        evt.StopPropagation(); // 立即停止事件传播, 防止HandleTab也被调用
        if(isNewLineChar)
            ApplySelectedCompletionAsync();
    }

    private void MoveCompletionSelection(int direction) {
        int currentIndex = Math.Clamp(completionListView.selectedIndex, 0, compList.Count - 1);
        int nextIndex = Math.Clamp(currentIndex + direction, 0, compList.Count - 1);
        completionListView.SetSelection(nextIndex);
        completionListView.ScrollToItem(nextIndex);
    }

    /// <summary>
    /// 应用选择的补全候选项, 应用完成后隐藏补全候选项列表
    /// </summary>
    private async void ApplySelectedCompletionAsync() {
        int selectedIndex = completionListView.selectedIndex;
        if(selectedIndex < 0 || selectedIndex >= compList.Count) return;

        CompletionItem selectedItem = compList[selectedIndex];
        HandleHideCompletionList();

        try {
            CompletionApplyResult result = await completion.ApplyCompletionAsync(selectedItem);

            if(result == null) return;

            codeInputField.SetValueWithoutNotify(result.code);

            if(highlightEnabledToggle.value)
                codeHighlightLabel.text = codeHighLighter.Highlight(result.code, codeExec);
            else
                codeHighlightLabel.text = result.code;
            
            int cursorIndex = Math.Clamp(result.cursorIndex, 0, result.code.Length);

            // codeInputField.Focus();
            codeInputField.SelectRange(cursorIndex, cursorIndex);
            // codeInputField.schedule.Execute(() => {
            //     codeInputField.Focus();
            //     codeInputField.SelectRange(cursorIndex, cursorIndex);
            // });
        }catch(Exception){}
    }

    private void HandleCursorMove(KeyDownEvent evt) {
        switch (evt.keyCode) {
            case KeyCode.LeftArrow:
            case KeyCode.RightArrow:
            case KeyCode.Home:
            case KeyCode.End: //
                codeInputField.schedule.Execute(UpdateCompletionListPosition);
                break;
        }
    }

    private void HandleHighlightEnabled(ChangeEvent<bool> changeEvent) {
        if (changeEvent.newValue) {
            codeHighlightLabel.enableRichText = true;
            string highlightText = codeHighLighter.Highlight(codeInputField.text, codeExec);
            codeHighlightLabel.text = highlightText;
        }
        else {
            codeHighlightLabel.enableRichText = false;
            codeHighlightLabel.text = codeInputField.text;
        }
        EditorUserSettings.SetConfigValue(HighlightEnabled, changeEvent.newValue ? "1" : "0");
    }
}
