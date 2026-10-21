using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Kei.Term.App.Helpers;

namespace Kei.Term.App.Views.Sync;

/// <summary>
/// 输入/更换同步口令弹窗（口令遮蔽，包含安全提示，回车提交，Esc 取消）
/// </summary>
public sealed class SyncPassphrasePromptWindow : Window
{
    private readonly TextBox _input;

    public SyncPassphrasePromptWindow(bool changing, string? error = null)
    {
        Title = changing ? "更换同步口令" : "输入同步口令";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        ShowInTaskbar = false;
        Bind(BackgroundProperty, this.GetResourceObservable("Kei.Bg.Panel"));
        Bind(FontFamilyProperty, this.GetResourceObservable("Kei.Font.UI"));

        var header = new TextBlock
        {
            Text = changing ? "设置新的端到端加密同步口令" : "需要同步口令以解密远端同步包",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        header.Bind(TextBlock.ForegroundProperty, header.GetResourceObservable("Kei.Text.Primary"));

        var description = new TextBlock
        {
            Text = changing
                ? "更换口令将先以旧口令完成同步，然后使用新口令重新加密上传并清空远端历史版本。注意：更换口令无法撤回其他设备已下载的数据，也不能撤销云存储账号的访问权限。"
                : "远端同步包使用端到端加密，请输入设置该同步包时指定的同步口令。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        description.Bind(TextBlock.ForegroundProperty, description.GetResourceObservable("Kei.Text.Secondary"));

        var errText = new TextBlock
        {
            Text = error ?? string.Empty,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            IsVisible = !string.IsNullOrEmpty(error)
        };
        errText.Bind(TextBlock.ForegroundProperty, errText.GetResourceObservable("Kei.Status.Error"));

        _input = new TextBox
        {
            PasswordChar = '●',
            PlaceholderText = changing ? "输入新同步口令" : "输入同步口令",
            MinWidth = 320
        };

        var showPassCheck = new CheckBox
        {
            Content = "显示明文口令",
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 12)
        };
        showPassCheck.IsCheckedChanged += (_, _) =>
        {
            _input.PasswordChar = showPassCheck.IsChecked == true ? '\0' : '●';
        };

        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Close(_input.Text);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
            }
        };

        var cancelBtn = new Button
        {
            Content = Strings.Get("Common.Cancel"),
            MinWidth = 80,
            Height = 28,
            IsCancel = true,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        cancelBtn.Click += (_, _) => Close(null);

        var confirmBtn = new Button
        {
            Content = Strings.Get("Common.Confirm"),
            MinWidth = 80,
            Height = 28,
            IsDefault = true,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        confirmBtn.Classes.Add("accent");
        confirmBtn.Click += (_, _) => Close(_input.Text);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
            }
        };

        Opened += (_, _) =>
        {
            _input.Focus();
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18, 20, 16),
            Children =
            {
                header,
                description,
                errText,
                _input,
                showPassCheck,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancelBtn, confirmBtn }
                }
            }
        };
    }
}
