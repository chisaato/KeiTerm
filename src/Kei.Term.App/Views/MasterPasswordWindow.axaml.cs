using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

// 保留传统密码结果，同时为保险库解锁提供密码 / 本机验证 / 取消三种选择。
public partial class MasterPasswordWindow : Window
{
    private bool _usesTypedResponse;

    public MasterPasswordWindow()
    {
        InitializeComponent();

        // 快捷入口为默认动作时，密码输入仍必须将 Enter 路由到密码验证。
        PasswordBox.AddHandler(KeyDownEvent, OnPasswordKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        Opened += (_, _) =>
        {
            if (QuickUnlockButton.IsVisible) QuickUnlockButton.Focus();
            else PasswordBox.Focus();
        };

        KeyDown += (sender, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                e.Handled = true;
                Cancel();
            }
        };
    }

    // error：上次解锁失败的提示（为空则不显示）
    public MasterPasswordWindow(string? error) : this()
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
        }

        PasswordBox.Focus();
    }

    // 启用本机验证前的密码确认：说明放在普通提示区，结果仍沿用字符串密码接口。
    public static MasterPasswordWindow CreatePasswordConfirmationDialog(string? message)
    {
        MasterPasswordWindow dialog = new(error: null);
        dialog.Title = Strings.Get("MasterPassword.ConfirmationTitle");
        dialog.HeaderText.Text = Strings.Get("MasterPassword.ConfirmationTitle");
        dialog.DescriptionText.Text = Strings.Get("MasterPassword.ConfirmationDescription");
        dialog.PasswordUnlockButton.Content = Strings.Get("MasterPassword.ConfirmationAction");
        if (!string.IsNullOrWhiteSpace(message)) dialog.HintText.Text = message;
        return dialog;
    }

    // 工厂避免与既有 new MasterPasswordWindow(null) 的字符串构造产生歧义。
    public static MasterPasswordWindow CreateVaultUnlockDialog(VaultUnlockPrompt prompt)
    {
        MasterPasswordWindow dialog = new(prompt.Error) { _usesTypedResponse = true };
        if (!string.IsNullOrWhiteSpace(prompt.Notice)) dialog.HintText.Text = prompt.Notice;
        if (prompt.CanQuickUnlock && !string.IsNullOrWhiteSpace(prompt.QuickUnlockDisplayName))
        {
            dialog.QuickUnlockButton.Content = Strings.Format("MasterPassword.QuickUnlock", prompt.QuickUnlockDisplayName);
            dialog.QuickUnlockButton.IsVisible = true;
            dialog.QuickUnlockSection.IsVisible = true;
            dialog.QuickUnlockButton.IsDefault = true;
            dialog.PasswordUnlockButton.IsDefault = false;
            dialog.PasswordUnlockButton.Classes.Remove("accent");
        }
        return dialog;
    }

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnConfirm(sender, e);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        string password = PasswordBox.Text ?? string.Empty;
        if (password.Length == 0)
        {
            ErrorText.Text = Strings.Get("MasterPassword.Error.Required");
            ErrorText.IsVisible = true;
            return;
        }

        PasswordBox.Text = null;
        if (_usesTypedResponse)
        {
            Close(new VaultUnlockResponse(VaultUnlockKind.Password, password));
        }
        else
        {
            Close(password);
        }
    }

    private void OnQuickUnlock(object? sender, RoutedEventArgs e)
    {
        if (!_usesTypedResponse || !QuickUnlockButton.IsVisible) return;
        PasswordBox.Text = null;
        Close(new VaultUnlockResponse(VaultUnlockKind.QuickUnlock));
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Cancel();
    }

    private void Cancel()
    {
        PasswordBox.Text = null;
        if (_usesTypedResponse) Close(new VaultUnlockResponse(VaultUnlockKind.Cancelled));
        else Close(null);
    }
}
