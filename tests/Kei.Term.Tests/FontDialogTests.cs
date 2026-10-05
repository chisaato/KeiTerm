using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.Tests;

// 字体弹窗只改已有 TerminalProfile 的字重和行距，不另建字体模型。
public class FontDialogTests
{
    [Fact]
    public void Confirm_WritesWeightAndLineHeight()
    {
        TerminalProfile profile = new()
        {
            FontWeight = "Normal",
            LineHeight = 1.2
        };
        FontDialogViewModel dialog = new(profile)
        {
            FontWeight = "Bold",
            LineHeight = 1.25
        };

        dialog.Confirm();

        Assert.True(dialog.IsConfirmed);
        Assert.Equal("Bold", profile.FontWeight);
        Assert.Equal(1.25, profile.LineHeight);
    }

    [Fact]
    public void Cancel_DoesNotWriteBack()
    {
        TerminalProfile profile = new()
        {
            FontWeight = "Regular",
            LineHeight = 1.3
        };
        FontDialogViewModel dialog = new(profile)
        {
            FontWeight = "Bold",
            LineHeight = 1.25
        };

        dialog.Cancel();

        Assert.False(dialog.IsConfirmed);
        Assert.Equal("Regular", profile.FontWeight);
        Assert.Equal(1.3, profile.LineHeight);
    }
}
