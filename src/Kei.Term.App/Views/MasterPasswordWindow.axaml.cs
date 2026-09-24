using Avalonia.Controls;
using Avalonia.Interactivity;
using Kei.Term.App.Helpers;

namespace Kei.Term.App.Views;

// 主密码输入框：密码仅内存流转，确认回传明文，取消回传 null
public partial class MasterPasswordWindow : Window
{
    public MasterPasswordWindow()
    {
        InitializeComponent();

        KeyDown += (sender, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                e.Handled = true;
                Close(null);
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

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Text ?? string.Empty;
        if (password.Length == 0)
        {
            ErrorText.Text = Strings.Get("MasterPassword.Error.Required");
            ErrorText.IsVisible = true;
            return;
        }

        Close(password);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
