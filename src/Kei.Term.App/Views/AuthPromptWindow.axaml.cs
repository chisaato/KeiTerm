using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

// 统一认证窗（XShell 风格）：
//  - 完整模式：密码 / 公钥（文件或保管库密钥）/ 交互式，三种方式共用用户名与窗口
//  - 提示模式：KI 真交互回调的轻量变体，仅服务器提示文本 + 单行输入
public partial class AuthPromptWindow : Window
{
    // 进程内记住上次选择的方法
    private static AuthPromptMethod _lastMethod = AuthPromptMethod.Password;

    private static readonly AuthPromptMethod[] MethodOrder =
    {
        AuthPromptMethod.Password,
        AuthPromptMethod.PublicKeyFile,
        AuthPromptMethod.Interactive
    };

    private static string[] MethodLabels =>
    [
        Strings.Get("AuthPrompt.Method.Password"),
        Strings.Get("AuthPrompt.Method.PublicKey"),
        Strings.Get("AuthPrompt.Method.Interactive")
    ];

    private readonly bool _isPromptMode;

    private AuthPromptMethod _method = AuthPromptMethod.Password;

    // 公钥来源：索引 0 = 文件，其余为对应保管库密钥方法 Id
    private readonly List<Guid?> _keySourceVaultIds = [];

    public AuthPromptWindow()
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

    // 完整模式
    public AuthPromptWindow(
        string prefillUsername,
        IReadOnlyList<VaultKeyOption> vaultKeys,
        AuthPromptMethod? defaultMethod) : this()
    {
        UsernameBox.Text = prefillUsername;

        MethodCombo.ItemsSource = MethodLabels;
        var initial = defaultMethod ?? _lastMethod;
        MethodCombo.SelectedIndex = Math.Max(0, Array.IndexOf(MethodOrder, initial));

        // 密钥来源：先「文件…」，再逐个保管库密钥
        var sources = new List<string> { Strings.Get("AuthPrompt.KeySource.FromFile") };
        _keySourceVaultIds.Add(null);
        foreach (var key in vaultKeys)
        {
            sources.Add(key.DisplayName);
            _keySourceVaultIds.Add(key.MethodId);
        }
        KeySourceCombo.ItemsSource = sources;
        KeySourceCombo.SelectedIndex = 0;

        // 初始聚焦：密码模式聚焦密码框，其余聚焦用户名
        if (initial == AuthPromptMethod.Password)
        {
            PasswordBox.Focus();
        }
        else
        {
            UsernameBox.Focus();
        }
    }

    // 提示模式：promptText 为服务器下发的提示文本
    public AuthPromptWindow(string promptText) : this()
    {
        _isPromptMode = true;
        PromptPanel.IsVisible = true;
        FullPanel.IsVisible = false;
        PromptTextBlock.Text = promptText;
        PromptInputBox.Focus();
    }

    private void OnMethodChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (MethodCombo.SelectedIndex < 0 || MethodCombo.SelectedIndex >= MethodOrder.Length)
        {
            return;
        }

        _method = MethodOrder[MethodCombo.SelectedIndex];
        _lastMethod = _method;

        PasswordPanel.IsVisible = _method == AuthPromptMethod.Password;
        PublicKeyPanel.IsVisible = _method == AuthPromptMethod.PublicKeyFile;
        InteractiveHint.IsVisible = _method == AuthPromptMethod.Interactive;

        if (_method == AuthPromptMethod.Password)
        {
            PasswordBox.Focus();
        }
    }

    // 公钥来源切换：保管库密钥隐藏文件/口令字段
    private void OnKeySourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        var useVault = KeySourceCombo.SelectedIndex > 0;
        KeyFilePanel.IsVisible = !useVault;
        PassphrasePanel.IsVisible = !useVault;
        VaultKeyHint.IsVisible = useVault;
    }

    private async void OnBrowseKey(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Get("Common.SelectPrivateKeyFile"),
                AllowMultiple = false,
            });

            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                KeyPathBox.Text = path;
            }
        }
        catch
        {
            // 选择器不可用时允许手输路径
        }
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (_isPromptMode)
        {
            Close(PromptInputBox.Text ?? string.Empty);
            return;
        }

        var username = UsernameBox.Text ?? string.Empty;

        switch (_method)
        {
            case AuthPromptMethod.Password:
                Close(new AuthPromptResult(
                    AuthPromptMethod.Password,
                    username,
                    PasswordBox.Text ?? string.Empty,
                    null,
                    null,
                    null));
                return;

            case AuthPromptMethod.PublicKeyFile:
                if (KeySourceCombo.SelectedIndex > 0)
                {
                    var vaultId = _keySourceVaultIds[KeySourceCombo.SelectedIndex];
                    Close(new AuthPromptResult(
                        AuthPromptMethod.PublicKeyVault,
                        username,
                        null,
                        null,
                        null,
                        vaultId));
                    return;
                }

                var path = KeyPathBox.Text;
                if (string.IsNullOrWhiteSpace(path))
                {
                    ShowError(Strings.Get("AuthPrompt.Error.PathRequired"));
                    return;
                }

                var passphrase = PassphraseBox.Text;
                Close(new AuthPromptResult(
                    AuthPromptMethod.PublicKeyFile,
                    username,
                    null,
                    path.Trim(),
                    string.IsNullOrEmpty(passphrase) ? null : passphrase,
                    null));
                return;

            case AuthPromptMethod.Interactive:
            default:
                Close(new AuthPromptResult(
                    AuthPromptMethod.Interactive,
                    username,
                    null,
                    null,
                    null,
                    null));
                return;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
