using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.Tests;

// 字体弹窗只改已有 TerminalProfile 的字重和行距，不另建字体模型。
public class FontDialogTests
{
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
