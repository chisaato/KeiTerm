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

}
