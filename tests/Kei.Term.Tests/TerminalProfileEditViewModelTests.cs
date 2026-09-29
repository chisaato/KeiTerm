using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kei.Term.App.Models;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models.Profiles;
using Xunit;

namespace Kei.Term.Tests;

public class TerminalProfileEditViewModelTests
{
    [Fact]
    public void Constructor_WithBuiltInProfile_SetsCopyToTrueAndAppendsCopySuffix()
    {
        var builtIn = new TerminalProfile
        {
            Id = "builtin-test",
            Name = "Monokai",
            IsBuiltIn = true,
            Background = "#272822",
            Foreground = "#FFFFFF"
        };

        var vm = new TerminalProfileEditViewModel(builtIn);

        Assert.Equal("Monokai (副本)", vm.Name);
        Assert.False(vm.IsBuiltIn);
        Assert.Equal("#272822", vm.Background);
        Assert.Equal("#FFFFFF", vm.Foreground);
        Assert.Equal(16, vm.AnsiColors.Count);
        Assert.Equal(16, vm.AnsiBrushes.Count);
    }

    [Fact]
    public void SelectTargetAndChangeHex_UpdatesTargetColorAndBrush()
    {
        var vm = new TerminalProfileEditViewModel();

        // 选调 Background
        vm.SelectTarget("Background");
        vm.ActiveHex = "#123456";
        Assert.Equal("#123456", vm.Background);

        // 选调 Ansi4 (Blue)
        vm.SelectTarget("Ansi4");
        vm.ActiveHex = "#0000FF";
        Assert.Equal("#0000FF", vm.AnsiColors[4]);
    }

    [Fact]
    public void Confirm_ProducesValidResultProfileWithNewIdForCopies()
    {
        var original = new TerminalProfile
        {
            Id = "builtin-monokai",
            Name = "Monokai",
            IsBuiltIn = true
        };

        var vm = new TerminalProfileEditViewModel(original);
        vm.Name = "My New Theme";
        vm.Confirm();

        Assert.True(vm.IsConfirmed);
        Assert.NotEqual("builtin-monokai", vm.ResultProfile.Id);
        Assert.False(vm.ResultProfile.IsBuiltIn);
        Assert.Equal("My New Theme", vm.ResultProfile.Name);
        Assert.Equal(16, vm.ResultProfile.AnsiColors.Length);
    }

    [Fact]
    public void InvalidColorHex_BlocksConfirm_AndShowsValidationMessage()
    {
        var vm = new TerminalProfileEditViewModel();
        vm.SelectTarget("Background");
        vm.ActiveHex = "NotAHexColor";

        Assert.False(vm.IsCurrentColorValid);
        Assert.False(string.IsNullOrWhiteSpace(vm.ValidationMessage));

        // 尝试确认：由于非法状态应被拦截
        vm.Confirm();
        Assert.False(vm.IsConfirmed);

        // 纠正为有效色值
        vm.ActiveHex = "#112233";
        Assert.True(vm.IsCurrentColorValid);
        Assert.True(string.IsNullOrWhiteSpace(vm.ValidationMessage));

        vm.Confirm();
        Assert.True(vm.IsConfirmed);
        Assert.Equal("#112233", vm.ResultProfile.Background);
    }

    [Fact]
    public void FontSnapshotConstructor_StoresSnapshot_AndRetainsLegacyFontFields()
    {
        var legacyProfile = new TerminalProfile
        {
            Id = "custom-1",
            Name = "Custom One",
            Background = "#1E1E1E",
            Foreground = "#EEEEEE",
            FontFamily = "Legacy Font",
            FontSize = 16.0,
            FontWeight = "Bold",
            IsItalic = true,
            LineHeight = 1.25,
            CursorBlink = false
        };

        var snapshot = new TerminalFontSnapshot(
            fontFamily: "Draft Cascadia",
            fallbackFonts: new List<string> { "Draft Fallback" },
            fontSize: 14.5,
            cursorBlink: true);

        var vm = new TerminalProfileEditViewModel(legacyProfile, snapshot);

        Assert.NotNull(vm.FontSnapshot);
        Assert.Equal("Draft Cascadia", vm.FontSnapshot.FontFamily);
        Assert.Equal(14.5, vm.FontSnapshot.FontSize);

        // 确认保存后，ResultProfile 应原样继承旧字体字段，未被抹除
        vm.Confirm();
        Assert.True(vm.IsConfirmed);
        Assert.Equal("Legacy Font", vm.ResultProfile.FontFamily);
        Assert.Equal(16.0, vm.ResultProfile.FontSize);
        Assert.Equal("Bold", vm.ResultProfile.FontWeight);
        Assert.True(vm.ResultProfile.IsItalic);
        Assert.Equal(1.25, vm.ResultProfile.LineHeight);
        Assert.False(vm.ResultProfile.CursorBlink);
    }

    [Fact]
    public void SettingsWindow_XamlAndCodeBehind_EnforceBusyAndNonForcedClosingContracts()
    {
        var solutionDir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(solutionDir) && !Directory.GetFiles(solutionDir, "*.sln*").Any())
        {
            solutionDir = Directory.GetParent(solutionDir)?.FullName;
        }
        var root = solutionDir ?? Environment.CurrentDirectory;

        var csFile = Path.Combine(root, "src", "Kei.Term.App", "Views", "SettingsWindow.axaml.cs");
        var xamlFile = Path.Combine(root, "src", "Kei.Term.App", "Views", "SettingsWindow.axaml");

        Assert.True(File.Exists(csFile), "SettingsWindow.axaml.cs 必须存在");
        Assert.True(File.Exists(xamlFile), "SettingsWindow.axaml 必须存在");

        var csText = File.ReadAllText(csFile);
        var xamlText = File.ReadAllText(xamlFile);

        // R1: 确保 TriggerCancelAndCloseAsync 的 finally 不包含无条件 Close() 和无条件 _isDischargingClose = true
        // 且仅在非 busy 状态下执行取消，busy 时拒绝退出
        Assert.Contains("if (vm.IsBusy)", csText);
        Assert.DoesNotContain("finally\n        {\n            _isDischargingClose = true;\n            Close();\n        }", csText.Replace("\r\n", "\n"));
        Assert.Contains("_isCancelling = false;", csText);

        // R2: 确保右侧表单与底部按钮均绑定了 !IsBusy，且错误文本单独呈现
        Assert.Contains("ScrollViewer IsEnabled=\"{Binding !IsBusy}\"", xamlText);
        Assert.Contains("IsEnabled=\"{Binding !IsBusy}\"", xamlText);
        Assert.Contains("NotificationTextBlock", xamlText);

        // R3: 确保 TerminalAppearanceSettingsPage 使用左右两栏分栏响应式布局（左侧表单+左下导入，右侧常驻实时Demo预览）
        Assert.Contains("DataTemplate DataType=\"settings:TerminalAppearanceSettingsPage\"", xamlText);
        Assert.Contains("ColumnDefinitions=\"400, *\"", xamlText);
        Assert.Contains("ColumnSpacing=\"20\"", xamlText);
        Assert.Contains("controls:TerminalShellPreviewView", xamlText);
        Assert.Contains("Profile=\"{Binding Appearance.SelectedTerminalProfile}\"", xamlText);
        Assert.Contains("Font=\"{Binding Appearance.DraftFontSnapshot}\"", xamlText);
        Assert.Contains("VerticalAlignment=\"Stretch\"", xamlText);
        Assert.Contains("MinHeight=\"360\"", xamlText);
        Assert.Contains("ImportKonsoleSchemeCommand", xamlText);
        Assert.Contains("从 Konsole 导入 (*.colorscheme)...", xamlText);
        Assert.Contains("从 iTerm2 导入 (预留)", xamlText);
        Assert.Contains("实时效果预览 (Live Preview)", xamlText);

        // 契约：设置页不再包含毫无用处的斜体勾选框
        Assert.DoesNotContain("IsItalic", xamlText);
        Assert.DoesNotContain("FontItalic", xamlText);
    }

    [Fact]
    public void TerminalShellPreviewView_CodeBehind_EnforcesCleanThemeTransitionAndSampleReplayContracts()
    {
        var solutionDir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(solutionDir) && !Directory.GetFiles(solutionDir, "*.sln*").Any())
        {
            solutionDir = Directory.GetParent(solutionDir)?.FullName;
        }
        var root = solutionDir ?? Environment.CurrentDirectory;

        var csFile = Path.Combine(root, "src", "Kei.Term.App", "Views", "Controls", "TerminalShellPreviewView.axaml.cs");
        Assert.True(File.Exists(csFile), "TerminalShellPreviewView.axaml.cs 必须存在");

        var csText = File.ReadAllText(csFile);

        // 1. 清屏序列包含 \x1b[2J 与 \x1b[3J（清空屏幕与回滚缓冲区）
        Assert.Contains(@"\x1b[2J", csText);
        Assert.Contains(@"\x1b[3J", csText);
        Assert.Contains(@"\x1b[0m\x1b[2J\x1b[3J\x1b[H", csText);

        // 2. 顺序约束：在 UpdateThemeAndFont 中，先 TerminalThemeAdapter.Apply，字体设置块之后再 WriteOutput(ClearScreenSequence)，重置守卫 _hasWrittenSample = false，再灌入样本与 InvalidateTerminal
        int updateThemeMethodIndex = csText.IndexOf("private void UpdateThemeAndFont()", StringComparison.Ordinal);
        Assert.True(updateThemeMethodIndex >= 0, "必须存在 UpdateThemeAndFont 方法");

        int applyIndex = csText.IndexOf("TerminalThemeAdapter.Apply(_terminalControl, Profile);", updateThemeMethodIndex, StringComparison.Ordinal);
        int fontIndex = csText.IndexOf("_terminalControl.FontFamilyName = primaryFamily;", updateThemeMethodIndex, StringComparison.Ordinal);
        int clearIndex = csText.IndexOf("_terminalControl.WriteOutput(ClearScreenSequence);", updateThemeMethodIndex, StringComparison.Ordinal);
        int resetGuardIndex = csText.IndexOf("_hasWrittenSample = false;", updateThemeMethodIndex, StringComparison.Ordinal);
        int sampleIndex = csText.IndexOf("EnsureSampleWritten();", updateThemeMethodIndex, StringComparison.Ordinal);
        int invalidateIndex = csText.IndexOf("_terminalControl.InvalidateTerminal();", updateThemeMethodIndex, StringComparison.Ordinal);

        Assert.True(applyIndex >= 0, "必须调用 TerminalThemeAdapter.Apply");
        Assert.True(fontIndex > applyIndex, "必须在 ApplyTheme 之后进行字体设置");
        Assert.True(clearIndex > fontIndex, "必须在 ApplyTheme 与字体设置均成功后向终端写入清屏序列");
        Assert.True(resetGuardIndex > clearIndex, "写入清屏后必须重置样本写入守卫以触发重放");
        Assert.True(sampleIndex > resetGuardIndex, "重置守卫后必须重新写入样本");
        Assert.True(invalidateIndex > sampleIndex, "样本重放后必须请求终端重绘");

        // 3. 边框 Letterbox 背景与主题 Background 绑定同步，消除边缘底色透出色差
        Assert.Contains("UpdateTerminalContainerBackground()", csText);
        Assert.Contains("container.Background = new SolidColorBrush(color)", csText);

        // 4. 样本结构与可读性断言：包含带索引的 ANSI 0..15 色条，主体文字用默认前景色呈现，CJK/EN 样本保留
        var sampleField = typeof(Kei.Term.App.Views.Controls.TerminalShellPreviewView)
            .GetField("DemoAnsiSample", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(sampleField);

        var sampleBytes = Assert.IsType<byte[]>(sampleField!.GetValue(null));
        string sampleText = System.Text.Encoding.UTF8.GetString(sampleBytes);

        // 包含每个色块的索引标注
        for (int i = 0; i <= 15; i++)
        {
            Assert.Contains(i.ToString(), sampleText);
        }

        // CJK/EN 语言标签与多语言内容
        Assert.Contains("[ZH]", sampleText);
        Assert.Contains("[JA]", sampleText);
        Assert.Contains("[KO]", sampleText);
        Assert.Contains("[EN]", sampleText);
        Assert.Contains("天地玄黄", sampleText);
        Assert.Contains("いろはにほへと", sampleText);
        Assert.Contains("다람쥐", sampleText);
        Assert.Contains("The quick brown fox jumps over the lazy dog.", sampleText);
        // 5. 光标闪烁驱动与生命周期断言：
        // 5.1 必须读取 Font.CursorBlink 属性
        Assert.Contains("Font?.CursorBlink", csText);
        // 5.2 必须使用 DispatcherTimer 设置 ≈530ms 周期并切换 Renderer.CursorVisible
        Assert.Contains("TimeSpan.FromMilliseconds(530)", csText);
        Assert.Contains("renderer.CursorVisible = visible", csText);
        Assert.Contains("_cursorBlinkPhase = !_cursorBlinkPhase;", csText);
        // 5.3 在 CleanupTerminalControl 中停止并释放计时器
        int cleanupIndex = csText.IndexOf("private void CleanupTerminalControl()", StringComparison.Ordinal);
        int stopInCleanupIndex = csText.IndexOf("StopCursorBlink();", cleanupIndex, StringComparison.Ordinal);
        Assert.True(cleanupIndex >= 0, "必须存在 CleanupTerminalControl 方法");
        Assert.True(stopInCleanupIndex > cleanupIndex, "CleanupTerminalControl 必须调用 StopCursorBlink 停止并解绑计时器");
        // 5.4 Renderer 尚未就绪（未挂载）时安全防护，并在 OnTerminalLoaded 中触发启动
        Assert.Contains("if (_terminalControl?.Renderer == null)", csText);
        int loadedIndex = csText.IndexOf("private void OnTerminalLoaded()", StringComparison.Ordinal);
        int blinkInLoadedIndex = csText.IndexOf("ApplyCursorBlink();", loadedIndex, StringComparison.Ordinal);
        Assert.True(loadedIndex >= 0, "必须存在 OnTerminalLoaded 方法");
        Assert.True(blinkInLoadedIndex > loadedIndex, "OnTerminalLoaded 必须在挂载就绪后调用 ApplyCursorBlink");
    }

    [Fact]
    public void TerminalShellPreviewView_WithoutVisualTree_DoesNotThrow()
    {
        // 针对未挂载/无可视化树时的实例化与属性更新契约：
        // Profile 为 null、Font 为 null 或包含 CursorBlink = true 时，不得抛出 NRE
        var view = new Kei.Term.App.Views.Controls.TerminalShellPreviewView();

        var snapshotWithBlink = new TerminalFontSnapshot(
            fontFamily: "Noto Sans Mono",
            fallbackFonts: [],
            fontSize: 14.0,
            cursorBlink: true);

        // 未进入可视化树时，_terminalControl 为 null，直接更新 Font/Profile 不得崩溃
        view.Font = snapshotWithBlink;
        view.Profile = new TerminalProfile();
        view.Font = null;
        view.Profile = null;
    }

    [Fact]
    public void TerminalShellPreviewView_StopCursorBlink_CleansUpTimerAndNullsField()
    {
        var view = new Kei.Term.App.Views.Controls.TerminalShellPreviewView();

        var timerField = typeof(Kei.Term.App.Views.Controls.TerminalShellPreviewView)
            .GetField("_cursorBlinkTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(timerField);

        // 人工模拟计时器已创建并运行
        var dummyTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(530)
        };
        dummyTimer.Start();
        timerField!.SetValue(view, dummyTimer);

        Assert.NotNull(timerField.GetValue(view));

        // 调用私有清理方法 CleanupTerminalControl()
        var cleanupMethod = typeof(Kei.Term.App.Views.Controls.TerminalShellPreviewView)
            .GetMethod("CleanupTerminalControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(cleanupMethod);
        cleanupMethod!.Invoke(view, null);

        // 真实断言：停表后计时器字段必须为 null 且已停止，杜绝计时器泄漏
        var afterCleanup = timerField.GetValue(view);
        Assert.Null(afterCleanup);
        Assert.False(dummyTimer.IsEnabled);
    }
}
