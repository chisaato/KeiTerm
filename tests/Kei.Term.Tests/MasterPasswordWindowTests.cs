using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.App.Views;

namespace Kei.Term.Tests;

public class MasterPasswordWindowTests
{
    [Fact]
    public Task EnterInPasswordInput_SubmitsTheUnmodifiedPassword() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        Window owner = new();
        MasterPasswordWindow dialog = new(null);
        try
        {
            owner.Show();
            Task<string?> result = dialog.ShowDialog<string?>(owner);
            HeadlessAvalonia.Pump();
            TextBox password = dialog.FindControl<TextBox>("PasswordBox")!;
            password.Text = " test-password ";
            Assert.True(password.Focus());

            // 经实际输入路由提交，验证密码框焦点不会吞掉默认按钮行为。
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            HeadlessAvalonia.Pump();

            Assert.True(result.IsCompletedSuccessfully);
            Assert.Equal(" test-password ", result.GetAwaiter().GetResult());
            Assert.False(dialog.IsVisible);
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    });

    [Fact]
    public Task EnterWithEmptyPassword_ShowsValidation_AndAllowsKeyboardRetry() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        Window owner = new();
        MasterPasswordWindow dialog = new("Previous attempt failed");
        try
        {
            owner.Show();
            Task<string?> result = dialog.ShowDialog<string?>(owner);
            HeadlessAvalonia.Pump();
            TextBox password = dialog.FindControl<TextBox>("PasswordBox")!;
            Assert.True(password.Focus());

            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            HeadlessAvalonia.Pump();

            Assert.False(result.IsCompleted);
            Assert.True(dialog.IsVisible);
            TextBlock error = dialog.FindControl<TextBlock>("ErrorText")!;
            Assert.True(error.IsVisible);
            Assert.Equal(Strings.Get("MasterPassword.Error.Required"), error.Text);

            password.Text = "retry-password";
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            HeadlessAvalonia.Pump();
            Assert.True(result.IsCompletedSuccessfully);
            Assert.Equal("retry-password", result.GetAwaiter().GetResult());
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    });

    [Fact]
    public Task EscapeWithPasswordInputFocused_CancelsWithoutSubmitting() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        Window owner = new();
        MasterPasswordWindow dialog = new(null);
        try
        {
            owner.Show();
            Task<string?> result = dialog.ShowDialog<string?>(owner);
            HeadlessAvalonia.Pump();
            TextBox password = dialog.FindControl<TextBox>("PasswordBox")!;
            password.Text = "test-password";
            Assert.True(password.Focus());

            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            HeadlessAvalonia.Pump();

            Assert.True(result.IsCompletedSuccessfully);
            Assert.Null(result.GetAwaiter().GetResult());
            Assert.False(dialog.IsVisible);
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    });
}
