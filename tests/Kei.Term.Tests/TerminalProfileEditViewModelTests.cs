using System;
using System.Linq;
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
}
