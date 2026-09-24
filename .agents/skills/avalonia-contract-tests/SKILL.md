---
name: avalonia-contract-tests
description: Use when writing, maintaining, or reviewing automated contract tests for Avalonia XAML styling, resource key integrity, and zero-raw-value enforcement in .NET desktop test suites
---

# Avalonia Contract Tests 规范

## 核心理念
光靠文档规范无法阻挡 AI 或开发者在赶进度时偷懒。桌面项目最有效的防腐手段不是文档，而是**自动化的源码与 XAML 契约测试（Contract Tests）**。

通过单元测试在测试期静态扫描 XAML 与 C# 代码，一旦发现违规写法，`dotnet test` 直接失败，从而筑起坚固的质量防腐墙。

---

## 1. 契约测试三大关键守卫

### 守卫一：禁止 View XAML 出现裸色号（No Raw Hex Color）
* **规则**：除了纯几何路径（Path Data）或特殊矢量资源外，所有界面的 `Background`、`Foreground`、`BorderBrush`、`Fill`、`Stroke` 必须使用 `{DynamicResource Kei.*}`。
* **防护逻辑**：正则扫描 `src/**/Views/**/*.axaml`，命中 `#[0-9a-fA-F]{3,8}` 即断言失败并输出具体文件名与行号。

### 守卫二：资源键名与设计令牌命名对齐（Resource Key Contract）
* **规则**：所有自定义资源键必须以项目统一前缀开头（如 `Kei.`），禁止随意起孤立命名（如 `MyBackground`、`CustomRed`）。
* **防护逻辑**：扫描 `Themes/*.axaml` 中的 `x:Key`，确保符合令牌命名空间层级（如 `Kei.Bg.*`、`Kei.Border.*`、`Kei.Text.*`、`Kei.Radius.*`）。

### 守卫三：本地化键强类型与对齐（Localization Contract）
* **规则**：
  1. C# 业务代码中严禁出现裸字符串本地化 Key（如 `Strings.Get("some.raw.key")`）；
  2. 多语言资源文件（如 `.resx`）的所有键必须在所有支持语言中完全对称，且复合格式占位符（如 `{0}`、`{1}`）必须一致。
* **防护逻辑**：反射扫描枚举/常量，并交叉比对各 resx 的条目集合。

---

## 2. 常见违规借口与事实反驳

| 常见借口 (Rationalization) | 实际规范 (Reality) |
| :--- | :--- |
| “XAML 又不是编译代码，写测试太小题大作了” | XAML 中的硬编码色号和拼错的 ResourceKey 往往在运行时静默降级或变透明，契约测试能 100% 静态拦截。 |
| “这个窗口是临时调试用的，先放行一次” | 临时代码最容易变成永久技术债，契约测试无条件一视同仁。 |
| “只要人工 Code Review 仔细看就能发现” | 人工审查极易漏看隐藏在多层嵌套标签里的裸色号，正则扫描只要 10ms 且零漏报。 |

---

## 3. 落地参考实现模板 (xUnit)

在 `tests/Kei.Term.Tests` 中建立 `Contracts/XamlContractTests.cs`：

```csharp
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Kei.Term.Tests.Contracts;

public class XamlContractTests
{
    private static readonly Regex HexColorRegex = new(@"\b(Background|Foreground|BorderBrush|Fill|Stroke)\s*=\s*""#(?:[0-9a-fA-F]{3,8})""", RegexOptions.Compiled);

    [Fact]
    public void Views_ShouldNotContainRawHexColors()
    {
        // 定位到工程中的 Views 目录
        var solutionDir = FindSolutionRoot();
        var viewsDir = Path.Combine(solutionDir, "src", "Kei.Term.App", "Views");
        if (!Directory.Exists(viewsDir)) return;

        var axamlFiles = Directory.GetFiles(viewsDir, "*.axaml", SearchOption.AllDirectories);
        var violations = (from file in axamlFiles
            let content = File.ReadAllText(file)
            where HexColorRegex.IsMatch(content)
            select Path.GetFileName(file)).ToList();

        Assert.True(violations.Count == 0, 
            $"以下 XAML 视图包含未令牌化的硬编码色号，请改用 {{DynamicResource Kei.*}}：{string.Join(", ", violations)}");
    }

    private static string FindSolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !Directory.GetFiles(dir, "*.sln*").Any())
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        return dir ?? Environment.CurrentDirectory;
    }
}
```

---

## 4. 运行与验证
将契约测试纳入常规 `dotnet test` 流水线中，让每次提交与代码重构都自动受到静态防腐保护。
