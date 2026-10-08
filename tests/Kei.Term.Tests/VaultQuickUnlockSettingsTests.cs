using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Vault;
using Microsoft.Data.Sqlite;

namespace Kei.Term.Tests;

public sealed class VaultQuickUnlockSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"keiterm_quickunlock_settings_{Guid.NewGuid():N}");
    private string Database => Path.Combine(_directory, "vault.db");
    private string Connection => $"Data Source={Database}";

    public VaultQuickUnlockSettingsTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public Task SettingsCommands_RequireMasterPassword_RejectFailedOrCancelledEnrollment_AndRevokeWithoutHardware()
        => HeadlessAvalonia.RunAsync(async () =>
    {
        await VaultTestDb.CreateAsync(Connection, Guid.NewGuid());
        using MemoryDeviceStore store = new();
        InternalVaultManager vault = new(Connection, quickUnlockStore: store);
        VaultQuickUnlockService service = new(vault, store);
        ScriptedInteraction interaction = new();
        VaultQuickUnlockSettingsViewModel page = new(service, () => interaction);
        await page.RefreshAsync();
        Assert.False(page.HasMasterPassword);
        Assert.False(page.EnableCommand.CanExecute(null));

        await vault.SetMasterPasswordAsync("master-password");
        vault.Lock();
        await page.RefreshAsync();
        Assert.True(page.EnableCommand.CanExecute(null));
        await page.EnableCommand.ExecuteAsync(null);
        Assert.False(page.IsEnabled);
        Assert.Equal(0, store.Count);
        Assert.False(vault.IsUnlocked);

        interaction.MasterPasswords.Enqueue("incorrect password");
        await page.EnableCommand.ExecuteAsync(null);
        Assert.True(page.HasError);
        Assert.False(page.IsBusy);
        Assert.Equal(0, store.Count);
        Assert.False(vault.IsUnlocked);

        interaction.MasterPasswords.Enqueue("master-password");
        await page.EnableCommand.ExecuteAsync(null);
        Assert.True(page.IsEnabled);
        Assert.False(page.HasError);
        Assert.False(page.EnableCommand.CanExecute(null));
        Assert.Equal(1, store.Count);
        vault.Lock();
        Assert.Equal(DeviceUnlockOutcome.Success, await service.TryUnlockAsync());
        Assert.True(vault.IsUnlocked);

        // 授权撤销不能依赖当前仍有指纹 / 硬件；移除后不能再用存储的密钥解锁。
        store.Available = false;
        await page.RefreshAsync();
        Assert.True(page.IsEnabled);
        Assert.False(page.IsAvailable);
        Assert.True(page.DisableCommand.CanExecute(null));
        await page.DisableCommand.ExecuteAsync(null);
        Assert.False(page.IsEnabled);
        Assert.Equal(0, store.Count);
        store.Available = true;
        vault.Lock();
        Assert.Equal(DeviceUnlockOutcome.NotEnrolled, await service.TryUnlockAsync());
        Assert.False(vault.IsUnlocked);
    });

    [Fact]
    public Task VisibleSettingsButton_EnrollsThroughOwnedPasswordDialog_AndGlobalCancelPreservesLocalEnrollment()
        => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        await VaultTestDb.CreateAsync(Connection, Guid.NewGuid());
        using MemoryDeviceStore store = new();
        InternalVaultManager vault = new(Connection, quickUnlockStore: store);
        await vault.SetMasterPasswordAsync("master-password");
        vault.Lock();
        VaultQuickUnlockService service = new(vault, store);
        FixedSettingsService settings = new();
        SettingsViewModel model = new(settings, _directory);
        model.ConfigureQuickUnlock(service);
        SettingsWindow window = new(model);
        try
        {
            window.Show();
            SettingsCategoryItem category = model.Categories.Single(item => item.Page is SshSettingsPage);
            model.SelectedCategory = category;
            SshSettingsPage ssh = (SshSettingsPage)category.Page;
            VaultQuickUnlockSettingsViewModel page = Assert.IsType<VaultQuickUnlockSettingsViewModel>(ssh.QuickUnlock);
            await model.RefreshQuickUnlockAsync();
            // Opened 也会异步探测能力，等待它完成后检查实际按钮启用状态。
            for (int attempt = 0; attempt < 100 && page.IsBusy; attempt++) await Task.Delay(10);
            Assert.False(page.IsBusy);
            Assert.True(page.HasLoaded);
            HeadlessAvalonia.Pump();
            Button enable = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "EnableQuickUnlockButton");
            enable.BringIntoView();
            HeadlessAvalonia.Pump();
            Assert.True(enable.IsEffectivelyVisible);
            Assert.True(enable.IsEnabled);
            Click(window, enable);

            MasterPasswordWindow? prompt = null;
            for (int attempt = 0; attempt < 100 && prompt == null; attempt++)
            {
                HeadlessAvalonia.Pump();
                prompt = window.OwnedWindows.OfType<MasterPasswordWindow>().SingleOrDefault();
                if (prompt == null) await Task.Delay(10);
            }
            Assert.NotNull(prompt);
            TextBlock hint = prompt.FindControl<TextBlock>("HintText")!;
            TextBlock error = prompt.FindControl<TextBlock>("ErrorText")!;
            Assert.True(hint.IsEffectivelyVisible);
            Assert.False(error.IsVisible);
            TextBox password = prompt.FindControl<TextBox>("PasswordBox")!;
            Assert.True(password.Focus());
            // 普通授权说明不应显示为错误；真正的空密码仍需触发验证并允许继续提交。
            prompt.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            HeadlessAvalonia.Pump();
            Assert.True(prompt.IsVisible);
            Assert.True(error.IsVisible);
            password.Text = "master-password";
            prompt.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            HeadlessAvalonia.Pump();
            Task enrollment = Assert.IsAssignableFrom<Task>(page.EnableCommand.ExecutionTask);
            await enrollment;
            Assert.True(page.IsEnabled);
            Assert.Equal(1, store.Count);

            await model.CancelCommand.ExecuteAsync(null);
            Assert.False(window.IsVisible);
            SettingsViewModel reopened = new(settings, _directory);
            reopened.ConfigureQuickUnlock(service);
            await reopened.RefreshQuickUnlockAsync();
            VaultQuickUnlockSettingsViewModel reopenedPage = reopened.Categories.Select(item => item.Page).OfType<SshSettingsPage>().Single().QuickUnlock!;
            Assert.True(reopenedPage.IsEnabled);
            vault.Lock();
            Assert.Equal(DeviceUnlockOutcome.Success, await service.TryUnlockAsync());
            Assert.True(vault.IsUnlocked);
        }
        finally
        {
            foreach (Window child in window.OwnedWindows.ToArray()) child.Close();
            window.Close();
        }
    });

    private static void Click(Window window, Button button)
    {
        Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    // 仅替换操作系统边界；测试中的保险库、密钥验证、服务与 UI 命令均使用生产实现。
    private sealed class MemoryDeviceStore : IDeviceQuickUnlockStore, IDisposable
    {
        private readonly Dictionary<string, byte[]> _keys = [];
        public string DisplayName => "Touch ID";
        public bool IsSupported => true;
        public bool Available { get; set; } = true;
        public int Count => _keys.Count;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);
        public Task<bool> HasKeyAsync(string keyId, CancellationToken ct = default) => Task.FromResult(_keys.ContainsKey(keyId));
        public Task StoreKeyAsync(string keyId, ReadOnlyMemory<byte> key, CancellationToken ct = default)
        {
            if (_keys.Remove(keyId, out byte[]? previous)) CryptographicOperations.ZeroMemory(previous);
            _keys.Add(keyId, key.ToArray());
            return Task.CompletedTask;
        }
        public Task<DeviceUnlockResult> UnlockKeyAsync(string keyId, string reason, CancellationToken ct = default)
            => Task.FromResult(_keys.TryGetValue(keyId, out byte[]? key)
                ? new DeviceUnlockResult(DeviceUnlockOutcome.Success, key.ToArray())
                : new DeviceUnlockResult(DeviceUnlockOutcome.NotEnrolled));
        public Task DeleteKeyAsync(string keyId, CancellationToken ct = default)
        {
            if (_keys.Remove(keyId, out byte[]? key)) CryptographicOperations.ZeroMemory(key);
            return Task.CompletedTask;
        }
        public void Dispose()
        {
            foreach (byte[] key in _keys.Values) CryptographicOperations.ZeroMemory(key);
            _keys.Clear();
        }
    }
}
