using Avalonia.Controls;
using Avalonia.Interactivity;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

// 保管库首次初始化选择框：设置主密码（推荐）或不加密存储（明文警示）
public partial class VaultSetupWindow : Window
{
    public VaultSetupWindow() : this(passwordOnly: false)
    {
    }

    // passwordOnly 用于更改已有主密码：只收集并确认新密码，不再提供明文选项。
    public VaultSetupWindow(bool passwordOnly)
    {
        InitializeComponent();
        if (passwordOnly)
        {
            Title = Strings.Get("Settings.Security.ChangeMasterPassword");
            HeaderText.Text = Strings.Get("Settings.Security.ChangeHeader");
            DescText.Text = Strings.Get("Settings.Security.ChangeDesc");
            MasterRadio.IsVisible = false;
            MasterDesc.IsVisible = false;
            PlainRadio.IsVisible = false;
            PlainDesc.IsVisible = false;
        }

        KeyDown += (sender, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                e.Handled = true;
                Close(new VaultSetupResult(VaultSetupChoice.Cancel, null));
            }
        };
    }

    private void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        if (PasswordPanel == null)
        {
            return;
        }

        PasswordPanel.IsVisible = MasterRadio.IsChecked == true;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (MasterRadio.IsChecked == true)
        {
            var password = PasswordBox.Text ?? string.Empty;
            var confirm = ConfirmBox.Text ?? string.Empty;

            if (password.Length == 0)
            {
                ShowError(Strings.Get("VaultSetup.Error.PasswordRequired"));
                return;
            }

            if (!string.Equals(password, confirm, System.StringComparison.Ordinal))
            {
                ShowError(Strings.Get("VaultSetup.Error.PasswordMismatch"));
                return;
            }

            Close(new VaultSetupResult(VaultSetupChoice.SetMasterPassword, password));
            return;
        }

        Close(new VaultSetupResult(VaultSetupChoice.PlainMode, null));
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(new VaultSetupResult(VaultSetupChoice.Cancel, null));
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
