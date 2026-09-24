using Avalonia.Controls;
using Avalonia.Interactivity;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Services;
using Kei.Term.Core.Vault;

namespace Kei.Term.App.Views;

// 文件私钥口令三态框：口令仅内存流转（Persistent 时由上层写入保管库）
public partial class PassphrasePromptWindow : Window
{
    private PassphrasePersistence _mode = PassphrasePersistence.AlwaysAsk;

    public PassphrasePromptWindow()
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

    public PassphrasePromptWindow(FilePrivateKeyMethod method) : this()
    {
        switch (method.PassphraseMode)
        {
            case PassphrasePersistence.SessionOnly:
                Setup(
                    DescribePath(method.KeyFilePath),
                    Strings.Get("PassphrasePrompt.Hint.SessionOnly"),
                    showRemember: true,
                    rememberDefault: true,
                    method.PassphraseMode);
                break;
            case PassphrasePersistence.Persistent:
                Setup(
                    DescribePath(method.KeyFilePath),
                    Strings.Get("PassphrasePrompt.Hint.Persistent"),
                    showRemember: false,
                    rememberDefault: false,
                    method.PassphraseMode);
                break;
            default:
                Setup(
                    DescribePath(method.KeyFilePath),
                    Strings.Get("PassphrasePrompt.Hint.AlwaysAsk"),
                    showRemember: false,
                    rememberDefault: false,
                    method.PassphraseMode);
                break;
        }
    }

    // Vault 私钥导入专用：口令随私钥一次性加密保存，之后取用无需再问
    public static PassphrasePromptWindow ForVaultImport(string keyFilePath)
    {
        var window = new PassphrasePromptWindow();
        window.Setup(
            DescribePath(keyFilePath),
            Strings.Get("PassphrasePrompt.Hint.VaultImport"),
            showRemember: false,
            rememberDefault: false,
            PassphrasePersistence.AlwaysAsk);
        return window;
    }

    private void Setup(string contextText, string hintText, bool showRemember, bool rememberDefault, PassphrasePersistence mode)
    {
        _mode = mode;
        KeyPathText.Text = contextText;
        ModeHintText.Text = hintText;
        RememberCheck.IsVisible = showRemember;
        RememberCheck.IsChecked = rememberDefault;
        PasswordBox.Focus();
    }

    private static string DescribePath(string? keyFilePath)
        => string.IsNullOrWhiteSpace(keyFilePath)
            ? Strings.Get("PassphrasePrompt.KeyPathEmpty")
            : string.Format(Strings.Get("PassphrasePrompt.KeyPathFormat"), keyFilePath);

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var passphrase = PasswordBox.Text ?? string.Empty;
        if (passphrase.Length == 0)
        {
            ErrorText.Text = Strings.Get("PassphrasePrompt.Error.Required");
            ErrorText.IsVisible = true;
            return;
        }

        // Persistent 强制持久化；SessionOnly 由复选框决定；AlwaysAsk 不记忆
        var remember = _mode switch
        {
            PassphrasePersistence.Persistent => true,
            PassphrasePersistence.SessionOnly => RememberCheck.IsChecked == true,
            _ => false,
        };

        Close(new PassphrasePromptResult(passphrase, remember));
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
