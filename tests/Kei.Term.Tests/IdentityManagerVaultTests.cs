using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kei.Term.Tests;

public class IdentityManagerVaultTests
{
    [Fact]
    public Task RightHandVaultButtons_UnlockAndLockSecrets_AndClearSessionPassphrase() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        UiDesignSystemService.Apply();
        IdentityManagerWindow window = new(fixture.Manager);
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            Button unlock = window.FindControl<Button>("UnlockVaultButton")!;
            Button lockButton = window.FindControl<Button>("LockVaultButton")!;
            Assert.True(unlock.IsEffectivelyVisible);
            Assert.False(lockButton.IsVisible);
            AssertAtRightEdge(window, unlock);
            await CapturePreviewAsync(window, "vault-locked");

            fixture.Interaction.MasterPasswords.Enqueue(Fixture.MasterPassword);
            Click(window, unlock);
            await fixture.Manager.UnlockVaultCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(5));
            HeadlessAvalonia.Pump();
            Assert.Equal("stored secret", (await fixture.Vault.GetSecretsAsync(fixture.IdentityId))["password"].Password);
            Assert.True(lockButton.IsEffectivelyVisible);
            Assert.False(unlock.IsVisible);
            AssertAtRightEdge(window, lockButton);
            await CapturePreviewAsync(window, "vault-unlocked");

            FilePrivateKeyMethod method = new() { PassphraseMode = PassphrasePersistence.SessionOnly };
            fixture.Interaction.Passphrases.Enqueue(new PassphrasePromptResult("remembered passphrase", true));
            await fixture.Session.PromptPassphraseAsync(method);
            Assert.Equal("remembered passphrase", fixture.Session.GetSessionPassphrase(method.Id));

            Click(window, lockButton);
            Assert.False(fixture.Vault.IsUnlocked);
            Assert.Null(fixture.Session.GetSessionPassphrase(method.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Vault.GetSecretsAsync(fixture.IdentityId));
            Assert.True(unlock.IsEffectivelyVisible);
            Assert.False(lockButton.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task CancelledUnlock_LeavesTheVaultLocked_AndAllowsRetry() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Manager.UnlockVaultCommand.ExecuteAsync(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Vault.GetSecretsAsync(fixture.IdentityId));
        Assert.True(fixture.Manager.CanUnlockVault);
        Assert.False(fixture.Manager.CanLockVault);

        fixture.Interaction.MasterPasswords.Enqueue(Fixture.MasterPassword);
        await fixture.Manager.UnlockVaultCommand.ExecuteAsync(null);
        Assert.Equal("stored secret", (await fixture.Vault.GetSecretsAsync(fixture.IdentityId))["password"].Password);
        Assert.True(fixture.Manager.CanLockVault);
    });

    [Fact]
    public Task SessionUnlockAndIdleLock_UpdateAnAlreadyOpenManager() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        UiDesignSystemService.Apply();
        IdentityManagerWindow window = new(fixture.Manager);
        try
        {
            window.Show();
            fixture.Interaction.MasterPasswords.Enqueue(Fixture.MasterPassword);
            Assert.True(await fixture.Session.EnsureUnlockedAsync());
            HeadlessAvalonia.Pump();
            Assert.True(window.FindControl<Button>("LockVaultButton")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Button>("UnlockVaultButton")!.IsVisible);

            Assert.True(fixture.Session.AutoLockIfIdle(fixture.Session.LastAccessUtc.AddMinutes(5)));
            HeadlessAvalonia.Pump();
            Assert.True(window.FindControl<Button>("UnlockVaultButton")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Button>("LockVaultButton")!.IsVisible);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task CancelledEditor_RefreshesAnyUnlockPerformedForThePreview(bool newIdentity) => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Manager.OpenEditDialogAsync = async _ =>
        {
            await fixture.Vault.UnlockAsync(Fixture.MasterPassword, false);
            return null;
        };
        if (newIdentity) await fixture.Manager.AddIdentityCommand.ExecuteAsync(null);
        else await fixture.Manager.EditIdentityCommand.ExecuteAsync(null);

        Assert.True(fixture.Manager.CanLockVault);
        fixture.Manager.LockVaultCommand.Execute(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Vault.GetSecretsAsync(fixture.IdentityId));
    });

    [Fact]
    public Task ManualUnlockDialog_IsOwnedByTheOpenManager_AndAcceptsPassword() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        UiDesignSystemService.Apply();
        MainViewModel main = new(new SqliteTreeRepository(fixture.ConnectionString), fixture.Repository,
            fixture.Vault, fixture.Vault, fixture.Settings, new SshSessionFactory());
        MainWindow owner = new() { DataContext = main };
        owner.WireDialogs(main, fixture.Manager, new SettingsViewModel(fixture.Settings));
        IdentityManagerWindow managerWindow = new(fixture.Manager);
        try
        {
            owner.Show();
            Task managerClosed = managerWindow.ShowDialog(owner);
            HeadlessAvalonia.Pump();
            Task unlocking = fixture.Manager.UnlockVaultCommand.ExecuteAsync(null);
            HeadlessAvalonia.Pump();
            MasterPasswordWindow passwordWindow = Assert.Single(managerWindow.OwnedWindows.OfType<MasterPasswordWindow>());
            Assert.True(passwordWindow.IsVisible);
            Assert.Empty(owner.OwnedWindows.OfType<MasterPasswordWindow>());
            passwordWindow.FindControl<TextBox>("PasswordBox")!.Text = Fixture.MasterPassword;
            Click(passwordWindow, passwordWindow.FindControl<Button>("PasswordUnlockButton")!);
            await unlocking.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("stored secret", (await fixture.Vault.GetSecretsAsync(fixture.IdentityId))["password"].Password);
            managerWindow.Close();
            await managerClosed;
        }
        finally
        {
            managerWindow.Close();
            owner.Close();
            await main.DisposeAsync();
        }
    });

    private static void AssertAtRightEdge(Window window, Button button)
    {
        Point position = button.TranslatePoint(default, window)!.Value;
        Assert.InRange(window.Bounds.Width - position.X - button.Bounds.Width, 13, 15);
        Button delete = window.FindControl<Button>("DeleteIdentityButton")!;
        Point deletePosition = delete.TranslatePoint(default, window)!.Value;
        Assert.True(position.X > deletePosition.X + delete.Bounds.Width + 20);
    }

    private static void Click(Window window, Button button)
    {
        Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        HeadlessAvalonia.Pump();
    }

    private static async Task CapturePreviewAsync(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KEITERM_VAULT_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        await Task.Delay(160);
        HeadlessAvalonia.Pump();
        using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered manager frame");
        frame.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private sealed class Fixture : IDisposable
    {
        public const string MasterPassword = "test master password";
        public required string Path { get; init; }
        public string ConnectionString => $"Data Source={Path};Pooling=False";
        public Guid IdentityId { get; } = Guid.NewGuid();
        public ScriptedInteraction Interaction { get; } = new();
        public FixedSettingsService Settings { get; } = new(new AppSettings { LockTimeoutMinutes = 5 });
        public InternalVaultManager Vault { get; private set; } = null!;
        public SqliteIdentityRepository Repository { get; private set; } = null!;
        public VaultSessionService Session { get; private set; } = null!;
        public IdentityManagerViewModel Manager { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new() { Path = VaultTestDb.NewPath() };
            await VaultTestDb.CreateAsync(fixture.ConnectionString, fixture.IdentityId);
            fixture.Vault = new(fixture.ConnectionString);
            fixture.Repository = new(fixture.ConnectionString);
            await fixture.Vault.SetMasterPasswordAsync(MasterPassword);
            await fixture.Vault.SaveSecretsAsync(fixture.IdentityId, new Dictionary<string, SecretPayload>
            {
                ["password"] = new() { Password = "stored secret" }
            });
            fixture.Vault.Lock();
            fixture.Session = new(fixture.Vault, fixture.Vault, fixture.Settings, () => fixture.Interaction);
            fixture.Manager = new(fixture.Repository, fixture.Vault, fixture.Vault);
            fixture.Manager.ConfigureVaultSession(fixture.Session);
            await fixture.Manager.LoadAsync();
            return fixture;
        }

        public void Dispose()
        {
            Vault.Lock();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(Path + suffix);
        }
    }
}
