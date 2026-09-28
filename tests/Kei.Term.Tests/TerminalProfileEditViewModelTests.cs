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
            isItalic: false,
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
    }
}
