using UnityEditor;
using UnityEngine.UIElements;
using UnityEngine;
using UnityEditor.UIElements;
using System;
using System.Text;

public class ConsoleEditor : EditorWindow {

    private const string FontPrefKey = "unity_fast_console_selected_font";
    private const string FontSizePrefKey = "unity_fast_console_selected_font_size";
    private const string LineSpeacingKey = "unity_fast_console_line_spacing";
    private static readonly int[] FontSizes = {10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20};
    private static readonly int[] LineSpacings = {0, 12, 24, 36, 48, 60, 72, 84, 96, 100};

    [SerializeField]
    private VisualTreeAsset uxml;

    private ToolbarButton executeButton;

    private ToolbarButton resetButton;

    private ToolbarMenu fontMenu;

    private ToolbarMenu fontSizeMenu;

    private ToolbarMenu lineSpacingMenu;

    private Label lineIndexLabel;

    private TextField codeInputField;

    private ScrollView codeInputScroller;

    // private float lineHeight;

    private int lastFirstLine = -1; // 上一次更新行号时, 第一行的编号

    [MenuItem("Tools/快速控制台2")]
    public static void OpenConsole() {
        GetWindow<ConsoleEditor>("C#控制台"); // 调用它打开窗口
    }

    public void CreateGUI() { // 打开窗口后的回调函数
        uxml.CloneTree(rootVisualElement);

        executeButton = rootVisualElement.Q<ToolbarButton>("toolbar_button");

        resetButton = rootVisualElement.Q<ToolbarButton>("reset_button");

        lineIndexLabel = rootVisualElement.Q<Label>("line_index");

        codeInputField = rootVisualElement.Q<TextField>("code_input");
        codeInputScroller = codeInputField.Q<ScrollView>();
        // 布局发生变化时算行高、更新行号
        codeInputField.RegisterCallback<GeometryChangedEvent>(evt => {
            HandleScroll();
        });
        // 滚动时更新行号
        codeInputScroller.verticalScroller.valueChanged += y => {
            HandleScroll();
        };

        fontMenu = rootVisualElement.Q<ToolbarMenu>("font_menu");
        RegisterFont(fontMenu);

        fontSizeMenu = rootVisualElement.Q<ToolbarMenu>("font_size_menu");
        RegisterFontSize(fontSizeMenu);

        lineSpacingMenu = rootVisualElement.Q<ToolbarMenu>("line_spacing_menu");
        RegisterLineSpacing(lineSpacingMenu);
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

    private void SetFont(string fontName) {
        EditorUserSettings.SetConfigValue(FontPrefKey, fontName);
        Font font = Font.CreateDynamicFontFromOSFont(fontName, 14);
        FontDefinition fontDef = FontDefinition.FromFont(font);
        if(font != null){
            lineIndexLabel.style.unityFontDefinition = fontDef;
            codeInputField.style.unityFontDefinition = fontDef;
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
        Debug.Log($"{height}, {lineHeight}, {visibleLines}, {firstLine}, {offsetY}");


        // 构建行号
        // 只有第一行发生变化时, 才更新行号
        if(firstLine != lastFirstLine){
            StringBuilder lineIndicesBuilder = new();
            for(int index = 1; index <= visibleLines; ++index){
                int lineIndex = firstLine + index;
                lineIndicesBuilder.AppendLine(lineIndex.ToString());
            }
            lineIndexLabel.text = lineIndicesBuilder.ToString();
        }
        lineIndexLabel.style.translate = new Translate(0, offsetY);
        lastFirstLine = firstLine;
    }
}
