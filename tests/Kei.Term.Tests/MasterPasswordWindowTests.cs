using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;

namespace Kei.Term.Tests;

public class MasterPasswordWindowTests
{
    [Fact]
    public Task EnterInPasswordInput_SubmitsTheUnmodifiedPassword() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        Window owner = new();
        MasterPasswordWindow dialog = new(null);
        try
        {
            owner.Show();
            Task<string?> result = dialog.ShowDialog<string?>(owner);
            HeadlessAvalonia.Pump();
            await CapturePreviewAsync(dialog, "password");
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
    public Task EnterWithEmptyPassword_ShowsValidation_AndAllowsKeyboardRetry() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        Window owner = new();
        MasterPasswordWindow dialog = new(Strings.Get("Status.Vault.PasswordIncorrect"));
        try
        {
            owner.Show();
            Task<string?> result = dialog.ShowDialog<string?>(owner);
            HeadlessAvalonia.Pump();
            await CapturePreviewAsync(dialog, "password-error");
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

    [Theory]
    [InlineData("Touch ID")]
    [InlineData("Windows Hello")]
    public Task QuickUnlockClick_ReturnsOnlyTheChoice_AndClearsAnyTypedPassword(string displayName) => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        Window owner = new();
        MasterPasswordWindow dialog = MasterPasswordWindow.CreateVaultUnlockDialog(new(CanQuickUnlock: true, QuickUnlockDisplayName: displayName));
        try
        {
            owner.Show();
            Task<VaultUnlockResponse?> result = dialog.ShowDialog<VaultUnlockResponse?>(owner);
            HeadlessAvalonia.Pump();
            TextBox password = dialog.FindControl<TextBox>("PasswordBox")!;
            password.Text = "partly entered password";
            Button quickUnlock = dialog.FindControl<Button>("QuickUnlockButton")!;
            Assert.True(quickUnlock.IsEffectivelyVisible);

            // 真实鼠标输入必须落到快捷按钮，且不会误触默认的密码提交按钮。
            Point center = quickUnlock.TranslatePoint(new Point(quickUnlock.Bounds.Width / 2, quickUnlock.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseDown(center, MouseButton.Left);
            dialog.MouseUp(center, MouseButton.Left);
            HeadlessAvalonia.Pump();

            Assert.True(result.IsCompletedSuccessfully);
            VaultUnlockResponse response = Assert.IsType<VaultUnlockResponse>(result.GetAwaiter().GetResult());
            Assert.Equal(VaultUnlockKind.QuickUnlock, response.Kind);
            Assert.Null(response.Password);
            Assert.True(string.IsNullOrEmpty(password.Text));
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    });

    [Theory]
    [InlineData(true, "Touch ID")]
    [InlineData(false, "Touch ID")]
    [InlineData(true, null)]
    public Task TypedUnlock_PreservesThePasswordPath_WithAndWithoutQuickUnlock(bool canQuickUnlock, string? displayName) => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        Window owner = new();
        MasterPasswordWindow dialog = MasterPasswordWindow.CreateVaultUnlockDialog(new(CanQuickUnlock: canQuickUnlock, QuickUnlockDisplayName: displayName));
        try
        {
            owner.Show();
            Task<VaultUnlockResponse?> result = dialog.ShowDialog<VaultUnlockResponse?>(owner);
            HeadlessAvalonia.Pump();
            Assert.Equal(canQuickUnlock && displayName != null, dialog.FindControl<Button>("QuickUnlockButton")!.IsVisible);
            TextBox password = dialog.FindControl<TextBox>("PasswordBox")!;
            Assert.True(password.Focus());
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            HeadlessAvalonia.Pump();
            Assert.False(result.IsCompleted);
            Assert.True(dialog.FindControl<TextBlock>("ErrorText")!.IsVisible);

            password.Text = " typed-password ";
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            HeadlessAvalonia.Pump();
            Assert.True(result.IsCompletedSuccessfully);
            VaultUnlockResponse response = Assert.IsType<VaultUnlockResponse>(result.GetAwaiter().GetResult());
            Assert.Equal(VaultUnlockKind.Password, response.Kind);
            Assert.Equal(" typed-password ", response.Password);
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    });

    [Fact]
    public Task TypedUnlock_EscapeReturnsCancellation_WithoutReturningTheEnteredPassword() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        Window owner = new();
        MasterPasswordWindow dialog = MasterPasswordWindow.CreateVaultUnlockDialog(new(CanQuickUnlock: true, QuickUnlockDisplayName: "Windows Hello"));
        try
        {
            owner.Show();
            Task<VaultUnlockResponse?> result = dialog.ShowDialog<VaultUnlockResponse?>(owner);
            HeadlessAvalonia.Pump();
            TextBox password = dialog.FindControl<TextBox>("PasswordBox")!;
            password.Text = "unsubmitted password";
            Assert.True(password.Focus());
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            HeadlessAvalonia.Pump();

            Assert.True(result.IsCompletedSuccessfully);
            VaultUnlockResponse response = Assert.IsType<VaultUnlockResponse>(result.GetAwaiter().GetResult());
            Assert.Equal(VaultUnlockKind.Cancelled, response.Kind);
            Assert.Null(response.Password);
            Assert.True(string.IsNullOrEmpty(password.Text));
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    });

    [Theory]
    [InlineData("Touch ID", "touch-id")]
    [InlineData("Windows Hello", "windows-hello")]
    public Task QuickUnlockEnter_FromInitialFocus_SelectsQuickUnlockWithoutSubmittingPassword(string displayName, string previewName)
        => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        Window owner = new();
        MasterPasswordWindow dialog = MasterPasswordWindow.CreateVaultUnlockDialog(new(CanQuickUnlock: true, QuickUnlockDisplayName: displayName));
        try
        {
            owner.Show();
            Task<VaultUnlockResponse?> result = dialog.ShowDialog<VaultUnlockResponse?>(owner);
            HeadlessAvalonia.Pump();
            Button quickUnlock = dialog.FindControl<Button>("QuickUnlockButton")!;
            TextBox password = dialog.FindControl<TextBox>("PasswordBox")!;
            Assert.True(quickUnlock.IsFocused);
            Assert.False(dialog.FindControl<TextBlock>("ErrorText")!.IsVisible);
            Assert.True(quickUnlock.TranslatePoint(default, dialog)!.Value.Y < password.TranslatePoint(default, dialog)!.Value.Y);
            await CapturePreviewAsync(dialog, previewName);

            // 初始 Enter 必须经过真实输入路由选择系统验证，不能落到空密码校验。
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            HeadlessAvalonia.Pump();
            Assert.True(result.IsCompletedSuccessfully);
            VaultUnlockResponse response = Assert.IsType<VaultUnlockResponse>(result.GetAwaiter().GetResult());
            Assert.Equal(VaultUnlockKind.QuickUnlock, response.Kind);
            Assert.Null(response.Password);
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    });

    [Fact]
    public Task ConfirmationMode_ShowsOrdinaryExplanation_AndPreservesPasswordSubmission() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        Window owner = new();
        MasterPasswordWindow dialog = MasterPasswordWindow.CreatePasswordConfirmationDialog(Strings.Get("VaultQuickUnlock.EnablePrompt"));
        try
        {
            owner.Show();
            Task<string?> result = dialog.ShowDialog<string?>(owner);
            HeadlessAvalonia.Pump();
            Assert.True(dialog.FindControl<TextBlock>("HintText")!.IsEffectivelyVisible);
            Assert.False(dialog.FindControl<TextBlock>("ErrorText")!.IsVisible);
            Assert.False(dialog.FindControl<Button>("QuickUnlockButton")!.IsVisible);
            TextBox password = dialog.FindControl<TextBox>("PasswordBox")!;
            Assert.True(password.IsFocused);
            await CapturePreviewAsync(dialog, "enrollment");

            password.Text = " confirmation-password ";
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            HeadlessAvalonia.Pump();
            Assert.True(result.IsCompletedSuccessfully);
            Assert.Equal(" confirmation-password ", result.GetAwaiter().GetResult());
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    });

    [Fact]
    public Task CancelledSystemVerification_ShowsNotice_AndStillValidatesPassword() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        Window owner = new();
        MasterPasswordWindow dialog = MasterPasswordWindow.CreateVaultUnlockDialog(new(
            CanQuickUnlock: true, QuickUnlockDisplayName: "Touch ID", Notice: Strings.Get("VaultQuickUnlock.Cancelled")));
        try
        {
            owner.Show();
            Task<VaultUnlockResponse?> result = dialog.ShowDialog<VaultUnlockResponse?>(owner);
            HeadlessAvalonia.Pump();
            Assert.Equal(Strings.Get("VaultQuickUnlock.Cancelled"), dialog.FindControl<TextBlock>("HintText")!.Text);
            Assert.False(dialog.FindControl<TextBlock>("ErrorText")!.IsVisible);
            await CapturePreviewAsync(dialog, "touch-id-cancelled");
            Assert.True(dialog.FindControl<TextBox>("PasswordBox")!.Focus());
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            HeadlessAvalonia.Pump();
            Assert.False(result.IsCompleted);
            Assert.True(dialog.FindControl<TextBlock>("ErrorText")!.IsVisible);
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    });

    private static async Task CapturePreviewAsync(MasterPasswordWindow dialog, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KEITERM_VAULT_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        await Task.Delay(160);
        HeadlessAvalonia.Pump();
        using WriteableBitmap frame = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered vault dialog frame");
        frame.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
