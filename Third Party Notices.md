# 第三方软件与资源声明

Unity Fast Console 使用了以下第三方软件和美术资源。

---

## 1. Microsoft .NET 编译器平台（Roslyn）

使用的组件包括：

- Microsoft.CodeAnalysis
- Microsoft.CodeAnalysis.CSharp
- Microsoft.CodeAnalysis.Scripting
- Microsoft.CodeAnalysis.CSharp.Scripting
- Microsoft.CodeAnalysis.Workspaces
- Microsoft.CodeAnalysis.CSharp.Workspaces
- Microsoft.CodeAnalysis.Features
- Microsoft.CodeAnalysis.CSharp.Features

版本：4.8.0

项目地址：

https://github.com/dotnet/roslyn

许可证地址：

https://github.com/dotnet/roslyn/blob/main/License.txt

版权所有：

Copyright (c) .NET Foundation and Contributors

说明：

本项目使用 Roslyn 提供 C# 代码分析、编译、脚本执行、语法高亮和代码补全功能。

---

## 2. Visual Studio Code Icons

使用的资源：

- `Editor/icons/symbol-*.svg`

项目地址：

https://github.com/microsoft/vscode-icons

许可证地址：

https://creativecommons.org/licenses/by/4.0/

许可证：

Creative Commons Attribution 4.0 International  
知识共享署名 4.0 国际许可证（CC BY 4.0）

创作者：

Microsoft Corporation

署名声明：

本项目使用了 Microsoft Corporation 创建的 Visual Studio Code Icons 项目中的图标资源。这些图标依据 Creative Commons Attribution 4.0 International License 授权使用。


修改说明：

- 因unity VectorImage不支持原始图标中的"currentColor", 故原图标中的currentColor已全部改为实际呈现的颜色
