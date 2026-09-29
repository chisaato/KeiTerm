# 关于 RoyalTerminal 回退字体链（Fallback Font Chain）支持的分析与提议

> 本文档旨在梳理 KeiTerm 在接入 `RoyalApps.RoyalTerminal.Avalonia`（v0.5.2）过程中关于字体回退链的现状、限制与建议的上游扩展方案，供向上游提出 Issue / PR 时参考。

---

## 1. 背景与现状

在终端仿真器中，多语言混排（如 ASCII 英文 + 中日韩 CJK 字符 + Nerd Font / Emoji 符号）是高频场景。用户通常期望：
1. **主字体**（Primary Font）：使用专为代码设计的等宽英文/编程字体（如 `JetBrains Mono`、`Fira Code`、`Cascadia Code` 等）。
2. **回退字体链**（Fallback Font Chain）：当主字体缺少特定码点（例如汉字、平假名、谚文或特定符号）时，按优先级从回退列表（如 `Noto Sans Mono CJK SC`、`Microsoft YaHei`）依次匹配并提取字形进行渲染。

在 KeiTerm 的设置中，已为用户提供了**主等宽字体**以及**有序回退字体列表**（优先级从上至下、支持增删与上移下移）的配置界面。

---

## 2. 当前底层实现与限制

反编译并分析 `RoyalApps.RoyalTerminal`（0.5.2）渲染与文本整形模块：

### 2.1 控件层对外暴露单一字符串
在 `RoyalTerminal.Avalonia.Controls.TerminalControl` 中：
```csharp
public static readonly StyledProperty<string> FontFamilyNameProperty;
public string FontFamilyName { get; set; }
```
控件仅暴露了单一的 `FontFamilyName` 字符串属性，且不支持逗号分隔的复合回退链语法。

### 2.2 渲染引擎内部回退逻辑硬编码
在 `RoyalTerminal.Rendering.Skia.SkiaTerminalRenderer` 中：
```csharp
_fontResolver = new TerminalFontResolver(null);
```
内部构造 `TerminalFontResolver` 时传入 `null`，导致底层直接调用：
```csharp
_fontManager = fontManager ?? SKFontManager.CreateDefault();
```
当字符在主字体（`primaryTypeface`）中缺失时，`TerminalFontResolver` 走的是操作系统层面的单字遍历匹配：
```csharp
SKTypeface fallback = _fontManager.MatchCharacter(primaryFamilyName, fontWeight, fontWidth, fontSlant, bcp47, codepoint);
```

### 2.3 引发的问题
1. **无法遵从用户的回退偏好**：例如系统默认匹配到的 CJK 字体可能是非等宽的比例字体（如某些变体），导致 CJK 字符宽度与终端 2 列（Double-Width）单元格不匹配，产生明显的字距空隙或笔画横向挤压（Clamped 压缩）。
2. **符号与特殊字形不可控**：用户安装了特定修补版字体（Nerd Fonts、Powerline）作为回退时，底层无法优先命中这些字体，只能任由操作系统的全局字体池决定。

---

## 3. 建议的上游扩展方案（PR / Issue 提议）

为了在不破坏现有单一 `FontFamilyName` 兼容性的前提下支持回退字体链，建议对上游进行如下轻量级增强：

### 方案 A：在 `TerminalControl` 暴露回退字体集合属性（推荐）

1. **在 `TerminalControl` 增加 StyledProperty**：
   ```csharp
   public static readonly StyledProperty<IReadOnlyList<string>?> FallbackFontFamiliesProperty =
       AvaloniaProperty.Register<TerminalControl, IReadOnlyList<string>?>(nameof(FallbackFontFamilies));

   public IReadOnlyList<string>? FallbackFontFamilies
   {
       get => GetValue(FallbackFontFamiliesProperty);
       set => SetValue(FallbackFontFamiliesProperty, value);
   }
   ```

2. **在 `TerminalFontResolver` 中支持优先候选列表**：
   ```csharp
   public TerminalFontResolution ResolveFallback(SKTypeface primary, int codepoint, IReadOnlyList<string>? preferredFallbacks)
   {
       if (preferredFallbacks != null)
       {
           foreach (var familyName in preferredFallbacks)
           {
               var tf = _fontManager.MatchFamily(familyName);
               if (tf != null && tf.ContainsGlyph(codepoint))
               {
                   return new TerminalFontResolution(tf, UsedFallback: true);
               }
           }
       }
       // 回退到原有的系统级 MatchCharacter
       return ResolveSystemFallback(primary, codepoint);
   }
   ```

### 方案 B：在 `FontFamilyName` 中原生支持逗号分隔列表（零 API 变更）
如果在公共属性上希望保持最小化改动，也可在解析 `FontFamilyName` 时拆分逗号：
```csharp
// 例如 "JetBrains Mono, Noto Sans Mono CJK SC, monospace"
var families = FontFamilyName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
var primary = families.FirstOrDefault();
var fallbacks = families.Skip(1).ToList();
```
底层将第一个作为主字体，后续作为有序回退链传入 `TerminalFontResolver`。

---

## 4. KeiTerm 现状与临时规避建议

在底层库未合并上述特性之前：
- KeiTerm 设置界面保留回退字体列表配置，但在文档与提示中明确说明：“底层终端引擎当前仅激活单一主字体，多语言回退目前遵循操作系统全局字体偏好”。
- 推荐需要 CJK 完整等宽体验的用户，将主字体直接设置为包含等宽 CJK 的字体包（例如 `Sarasa Mono SC` / 更纱黑体、`Noto Sans Mono CJK SC` 等），可获得最佳排版效果。
