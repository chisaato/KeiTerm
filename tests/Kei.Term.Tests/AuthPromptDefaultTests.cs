using Avalonia.Controls;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;

namespace Kei.Term.Tests;

public class AuthPromptDefaultTests
{
    [Fact]
    public Task PreviousInteractiveSelection_DoesNotOverrideNextPasswordPrompt() => HeadlessAvalonia.RunAsync(() =>
    {
        var previous = new AuthPromptWindow("ops", [], AuthPromptMethod.Interactive);
        Assert.Equal(2, previous.FindControl<ComboBox>("MethodCombo")!.SelectedIndex);
        previous.Close();
        var next = new AuthPromptWindow("ops", [], null);
        Assert.Equal(0, next.FindControl<ComboBox>("MethodCombo")!.SelectedIndex);
        Assert.True(next.FindControl<Grid>("PasswordPanel")!.IsVisible);
        next.Close();
        return Task.CompletedTask;
    });
}
