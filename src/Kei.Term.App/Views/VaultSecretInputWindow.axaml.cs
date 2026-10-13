using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.Ssh.Services;

namespace Kei.Term.App.Views;

public partial class VaultSecretInputWindow : Window
{
    private readonly bool _privateKey;

    public VaultSecretInputWindow()
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

    public VaultSecretInputWindow(bool privateKey) : this()
    {
        _privateKey = privateKey;
        if (privateKey)
        {
            Title = Strings.Get("VaultSecretInput.Title.PrivateKey");
            HeaderBlock.Text = Strings.Get("VaultSecretInput.Title.PrivateKey");
            PasswordPanel.IsVisible = false;
            PrivateKeyPanel.IsVisible = true;
        }
        else
        {
            Title = Strings.Get("VaultSecretInput.Title.Password");
            HeaderBlock.Text = Strings.Get("VaultSecretInput.Title.Password");
            PasswordPanel.IsVisible = true;
            PrivateKeyPanel.IsVisible = false;
        }

        NameBox.Focus();
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
                KeyFilePathBox.Text = path;
                if (string.IsNullOrWhiteSpace(NameBox.Text))
                {
                    NameBox.Text = Path.GetFileName(path);
                }
            }
        }
        catch
        {
            // 选择器异常时保留手动输入
        }
    }

    private async void OnConfirm(object? sender, RoutedEventArgs e)
    {
        ConfirmButton.IsEnabled = false;
        try
        {
            string name = NameBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowError(Strings.Get("VaultSecretInput.Error.NameRequired"));
                return;
            }

            if (!_privateKey)
            {
                string password = PasswordBox.Text ?? string.Empty;
                if (string.IsNullOrEmpty(password))
                {
                    ShowError(Strings.Get("VaultSecretInput.Error.PasswordRequired"));
                    return;
                }

                Close(new VaultSecretDraft(
                    Name: name,
                    PrivateKey: false,
                    PrivateKeyContent: null,
                    Passphrase: null,
                    Password: password));
                return;
            }

            string path = KeyFilePathBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                ShowError(Strings.Get("VaultSecretInput.Error.FileRequired"));
                return;
            }

            var read = await PrivateKeyImport.ReadAsync(path);
            if (read == null || string.IsNullOrWhiteSpace(read.Content))
            {
                ShowError(Strings.Get("VaultSecretInput.Error.ReadFailed"));
                return;
            }

            string? passphrase = PassphraseBox.Text;
            if (string.IsNullOrEmpty(passphrase))
            {
                passphrase = null;
            }

            Close(new VaultSecretDraft(
                Name: name,
                PrivateKey: true,
                PrivateKeyContent: read.Content,
                Passphrase: passphrase,
                Password: null));
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            ConfirmButton.IsEnabled = true;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    // 任何关闭路径（确定/取消/Esc/标题栏）都在窗口消失前清空已输入的密钥材料
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        PasswordBox.Text = string.Empty;
        PassphraseBox.Text = string.Empty;
        base.OnClosing(e);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}