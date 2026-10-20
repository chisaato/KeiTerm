using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;

namespace Kei.Term.Tests;

public class IdentityEditPasswordTests
{
    [Fact]
    public Task PasswordInput_ApplyEncryptsAndCanMaterializeForSsh_WithoutPuttingSecretInIdentity() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        IdentityEditViewModel editor = fixture.Editor();
        editor.Name = "password identity";
        editor.Username = "tester";
        editor.AddMethodCommand.Execute(null);
        IdentityMethodRowViewModel row = Assert.Single(editor.Methods);
        UiDesignSystemService.Apply();
        IdentityEditWindow window = new(editor);
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            TextBox input = window.GetVisualDescendants().OfType<TextBox>()
                .Single(box => box.Classes.Contains("vaultPassword"));
            Assert.True(input.IsEffectivelyVisible);
            Assert.Equal('•', input.PasswordChar);
            Assert.False(input.RevealPassword);
            const string password = "  ssh secret 密码  ";
            input.Text = password;
            HeadlessAvalonia.Pump();
            CapturePreview(window);
            await editor.ApplyCommand.ExecuteAsync(null);
            HeadlessAvalonia.Pump();

            Assert.Null(editor.ErrorMessage);
            Assert.True(editor.WasApplied);
            Assert.True(row.HasSavedPassword);
            Assert.Empty(input.Text!);
            Identity stored = (await fixture.Repository.GetByIdAsync(editor.IdentityId))!;
            Assert.DoesNotContain(password, JsonSerializer.Serialize(stored));
            VaultPasswordMethod method = Assert.IsType<VaultPasswordMethod>(Assert.Single(stored.Methods));
            var raw = await VaultTestDb.ReadRawAsync(fixture.ConnectionString, stored.Id);
            Assert.DoesNotContain(password, Encoding.UTF8.GetString(raw.blob));
            Assert.True(raw.hasNonce);
            Assert.True(raw.hasTag);

            // 模拟下次连接：锁定后重新解锁并读取持久化材料，再走真实认证物化。
            fixture.Session.Lock();
            fixture.Interaction.MasterPasswords.Enqueue(Fixture.MasterPassword);
            FixedSettingsService settings = new(new AppSettings());
            AuthMaterialCollector collector = new(fixture.Repository, settings, fixture.Session, () => fixture.Interaction);
            ResolvedSessionConfig config = SessionConfigBuilder.Build(new SessionNode
            {
                Name = "SSH password test", Host = "test.invalid", Username = "tester", IdentityId = stored.Id,
            }, settings.Current);
            CollectedAuth collected = (await collector.CollectTargetAsync(new ConnectionRequest(config, UseIdentity: true)))!;
            MaterializedAuthMethod material = Assert.Single(collected.Materials);
            Assert.Equal(AuthMaterialKind.Password, material.Kind);
            Assert.Equal(password, material.Secret!.Password);
            Assert.Empty(fixture.Interaction.AuthPromptUsernames);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(null, "tester")]
    [InlineData("override", "override")]
    public async Task Collector_UsesIdentityUsername_UnlessSessionOverrides(string? sessionUser, string expected)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Identity identity = await fixture.SeedIdentityAsync();
        FixedSettingsService settings = new(new AppSettings { DefaultUsername = "global" });
        AuthMaterialCollector collector = new(fixture.Repository, settings, fixture.Session, () => fixture.Interaction);
        ResolvedSessionConfig config = SessionConfigBuilder.Build(new SessionNode
        {
            Name = "identity user", Host = "test.invalid", Username = sessionUser, IdentityId = identity.Id,
        }, settings.Current);

        CollectedAuth collected = (await collector.CollectTargetAsync(new ConnectionRequest(config, UseIdentity: true)))!;

        Assert.Equal(expected, collected.Config.Username);
    }

    [Fact]
    public async Task BlankNewPassword_PreventsSavingOrConfirming()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        IdentityEditViewModel editor = fixture.Editor();
        editor.AddMethodCommand.Execute(null);
        await editor.ConfirmCommand.ExecuteAsync(null);
        Assert.NotNull(editor.ErrorMessage);
        Assert.False(editor.IsConfirmed);
        Assert.False(editor.WasApplied);
        Assert.Null(await fixture.Repository.GetByIdAsync(editor.IdentityId));
    }

    [Fact]
    public async Task CancelledUnlock_DoesNotCreateIdentity_AndRetainsInputForRetry()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Session.Lock();
        IdentityEditViewModel editor = fixture.Editor();
        editor.AddMethodCommand.Execute(null);
        IdentityMethodRowViewModel row = Assert.Single(editor.Methods);
        row.Password = "retry secret";
        await editor.ConfirmCommand.ExecuteAsync(null);
        Assert.NotNull(editor.ErrorMessage);
        Assert.False(editor.IsConfirmed);
        Assert.Null(await fixture.Repository.GetByIdAsync(editor.IdentityId));
        Assert.Equal("retry secret", row.Password);

        fixture.Interaction.MasterPasswords.Enqueue(Fixture.MasterPassword);
        await editor.ConfirmCommand.ExecuteAsync(null);
        Assert.Null(editor.ErrorMessage);
        Assert.True(editor.IsConfirmed);
        Assert.Equal("retry secret", (await fixture.Vault.GetSecretsAsync(editor.IdentityId))[row.Model.Id.ToString()].Password);
        Assert.Empty(row.Password);
    }

    [Fact]
    public async Task Prepare_LockedVault_PromptsOnOpen_SoApplyDoesNotInterrupt()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Session.Lock();
        IdentityEditViewModel editor = fixture.Editor();
        fixture.Interaction.MasterPasswords.Enqueue(Fixture.MasterPassword);
        await editor.PrepareAsync();
        Assert.Equal(1, fixture.Interaction.MasterPasswordPrompts);
        Assert.True(fixture.Vault.IsUnlocked);

        editor.AddMethodCommand.Execute(null);
        IdentityMethodRowViewModel row = Assert.Single(editor.Methods);
        row.Password = "typed after unlock";
        await editor.ConfirmCommand.ExecuteAsync(null);
        Assert.True(editor.IsConfirmed);
        Assert.Equal(1, fixture.Interaction.MasterPasswordPrompts);
        Assert.Equal("typed after unlock", (await fixture.Vault.GetSecretsAsync(editor.IdentityId))[row.Model.Id.ToString()].Password);
    }

    [Fact]
    public async Task Prepare_CancelledUnlock_PromptsOnce_AndMarksVaultKeyUnknown()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Identity identity = new() { Name = "key identity", Methods = [new VaultPrivateKeyMethod()] };
        await fixture.Repository.SaveAsync(identity);
        await fixture.Session.PersistSecretAsync(identity.Id, identity.Methods[0].Id, new SecretPayload { PrivateKeyContent = "key material" });
        fixture.Session.Lock();
        IdentityEditViewModel editor = fixture.Editor(identity);
        editor.VaultKeyInfoLoader = fixture.Session.GetVaultKeyInfoAsync;
        await editor.PrepareAsync();
        // 取消后不能再为私钥预览二次弹框，也不能把已导入的私钥显示成「未导入」
        Assert.Equal(1, fixture.Interaction.MasterPasswordPrompts);
        IdentityMethodRowViewModel row = Assert.Single(editor.Methods);
        Assert.Equal(Kei.Term.App.Helpers.Strings.Get("IdentityEdit.VaultKey.Unknown"), row.VaultKeyStatus);
    }

    [Fact]
    public async Task CancelledReplacement_PreservesSavedPasswordAndMetadata()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Identity identity = await fixture.SeedIdentityAsync();
        fixture.Session.Lock();
        IdentityEditViewModel editor = fixture.Editor(identity);
        editor.Name = "cancelled rename";
        IdentityMethodRowViewModel row = Assert.Single(editor.Methods);
        row.Password = "cancelled replacement";
        await editor.ApplyCommand.ExecuteAsync(null);
        Assert.NotNull(editor.ErrorMessage);
        Assert.Equal("existing", (await fixture.Repository.GetByIdAsync(identity.Id))!.Name);
        await fixture.Vault.UnlockAsync(Fixture.MasterPassword, false);
        Assert.Equal(Fixture.OriginalPassword, (await fixture.Vault.GetSecretsAsync(identity.Id))[row.Model.Id.ToString()].Password);
        editor.CancelCommand.Execute(null);
        Assert.Empty(row.Password);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingPassword_BlankPreservesSecret_WithoutLoadingItIntoEditor(bool locked)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Identity identity = await fixture.SeedIdentityAsync();
        Guid methodId = identity.Methods[0].Id;
        var before = await VaultTestDb.ReadRawAsync(fixture.ConnectionString, identity.Id);
        if (locked) fixture.Session.Lock();
        IdentityEditViewModel editor = fixture.Editor(identity);
        await editor.InitializeVaultPasswordStatusAsync();
        IdentityMethodRowViewModel row = Assert.Single(editor.Methods);
        Assert.Empty(row.Password);
        Assert.Equal(locked ? (bool?)null : true, row.HasSavedPassword);
        editor.Description = "metadata only";
        await editor.ApplyCommand.ExecuteAsync(null);
        Assert.Null(editor.ErrorMessage);
        Assert.Equal(0, fixture.Interaction.MasterPasswordPrompts);
        Assert.Equal(before.blob, (await VaultTestDb.ReadRawAsync(fixture.ConnectionString, identity.Id)).blob);
        if (locked) await fixture.Vault.UnlockAsync(Fixture.MasterPassword, false);
        Assert.Equal(Fixture.OriginalPassword, (await fixture.Vault.GetSecretsAsync(identity.Id))[methodId.ToString()].Password);
    }

    [Fact]
    public async Task ReplacingAndClearingPassword_PreserveOtherMaterials_AndAllowRepeatedApply()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Identity identity = await fixture.SeedIdentityAsync();
        Guid passwordId = identity.Methods[0].Id;
        Guid keyId = Guid.NewGuid();
        await fixture.Session.PersistSecretAsync(identity.Id, keyId, new SecretPayload { PrivateKeyContent = "key material", Passphrase = "key phrase" });
        IdentityEditViewModel editor = fixture.Editor(identity);
        await editor.InitializeVaultPasswordStatusAsync();
        IdentityMethodRowViewModel row = Assert.Single(editor.Methods);
        row.Password = "replacement";
        await editor.ApplyCommand.ExecuteAsync(null);
        Assert.Null(editor.ErrorMessage);
        Assert.Equal("replacement", (await fixture.Vault.GetSecretsAsync(identity.Id))[passwordId.ToString()].Password);
        Assert.Empty(row.Password);

        row.RemoveVaultPasswordCommand.Execute(null);
        await editor.ApplyCommand.ExecuteAsync(null);
        await editor.ApplyCommand.ExecuteAsync(null);
        Assert.Null(editor.ErrorMessage);
        Dictionary<string, SecretPayload> secrets = await fixture.Vault.GetSecretsAsync(identity.Id);
        Assert.False(secrets.ContainsKey(passwordId.ToString()));
        Assert.Equal("key material", secrets[keyId.ToString()].PrivateKeyContent);
        Assert.Equal("key phrase", secrets[keyId.ToString()].Passphrase);
        Assert.False(row.HasSavedPassword);
    }

    [Fact]
    public async Task RemovingPasswordMethod_DeletesItsSecretAndPreservesRemainingMethod()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Identity identity = await fixture.SeedIdentityAsync();
        Guid passwordId = identity.Methods[0].Id;
        identity.Methods.Add(new InteractiveMethod { SortOrder = 1 });
        await fixture.Repository.SaveAsync(identity);
        IdentityEditViewModel editor = fixture.Editor(identity);
        IdentityMethodRowViewModel row = editor.Methods[0];
        row.Password = "discard this";
        editor.RemoveMethodCommand.Execute(row);
        await editor.ApplyCommand.ExecuteAsync(null);
        Assert.Null(editor.ErrorMessage);
        Assert.Empty(row.Password);
        Assert.False((await fixture.Vault.GetSecretsAsync(identity.Id)).ContainsKey(passwordId.ToString()));
        Assert.IsType<InteractiveMethod>(Assert.Single((await fixture.Repository.GetByIdAsync(identity.Id))!.Methods));
    }

    [Fact]
    public Task ClosingEditor_DiscardsPendingPasswordWithoutOverwritingSavedSecret() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Identity identity = await fixture.SeedIdentityAsync();
        IdentityEditViewModel editor = fixture.Editor(identity);
        IdentityMethodRowViewModel row = Assert.Single(editor.Methods);
        IdentityEditWindow window = new(editor);
        window.Show();
        row.Password = "discarded replacement";
        window.Close();
        Assert.Empty(row.Password);
        Assert.False(editor.WasApplied);
        Assert.Equal(Fixture.OriginalPassword, (await fixture.Vault.GetSecretsAsync(identity.Id))[row.Model.Id.ToString()].Password);
    });

    [Fact]
    public Task ManagerEditor_UsesSharedVaultSessionToSavePassword() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Identity identity = await fixture.SeedIdentityAsync();
        IdentityManagerViewModel manager = new(fixture.Repository, fixture.Vault, fixture.Vault);
        manager.ConfigureVaultSession(fixture.Session);
        await manager.LoadAsync();
        Window owner = new();
        IdentityManagerWindow managerWindow = new(manager);
        try
        {
            owner.Show();
            Task managerClosed = managerWindow.ShowDialog(owner);
            Task editing = manager.EditIdentityCommand.ExecuteAsync(null);
            HeadlessAvalonia.Pump();
            IdentityEditWindow editorWindow = Assert.Single(managerWindow.OwnedWindows.OfType<IdentityEditWindow>());
            IdentityEditViewModel editor = Assert.IsType<IdentityEditViewModel>(editorWindow.DataContext);
            TextBox input = editorWindow.GetVisualDescendants().OfType<TextBox>()
                .Single(box => box.Classes.Contains("vaultPassword"));
            input.Text = "password through manager";
            HeadlessAvalonia.Pump();
            await editor.ConfirmCommand.ExecuteAsync(null);
            await editing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(editor.IsConfirmed);
            Assert.Equal("password through manager", (await fixture.Vault.GetSecretsAsync(identity.Id))[identity.Methods[0].Id.ToString()].Password);
            managerWindow.Close();
            await managerClosed;
        }
        finally { managerWindow.Close(); owner.Close(); }
    });

    [Fact]
    public Task Saving_DisablesEditorAndDefersCloseUntilTheSecretIsPersisted() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        IdentityEditViewModel editor = fixture.Editor();
        editor.AddMethodCommand.Execute(null);
        IdentityMethodRowViewModel row = Assert.Single(editor.Methods);
        row.Password = "in-flight secret";
        TaskCompletionSource releaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.SaveIdentityAsync = async identity =>
        {
            await releaseWrite.Task;
            await fixture.Repository.SaveAsync(identity);
        };
        IdentityEditWindow window = new(editor);
        try
        {
            window.Show();
            Task applying = editor.ConfirmCommand.ExecuteAsync(null);
            HeadlessAvalonia.Pump();
            TextBox input = window.GetVisualDescendants().OfType<TextBox>()
                .Single(box => box.Classes.Contains("vaultPassword"));
            Assert.False(input.IsEffectivelyEnabled);
            window.Close();
            Assert.True(window.IsVisible);
            releaseWrite.SetResult();
            await applying.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(window.IsVisible);
            Assert.Equal("in-flight secret", (await fixture.Vault.GetSecretsAsync(editor.IdentityId))[row.Model.Id.ToString()].Password);
        }
        finally { releaseWrite.TrySetResult(); window.Close(); }
    });

    private static void CapturePreview(Window window)
    {
        string? directory = Environment.GetEnvironmentVariable("KEITERM_IDENTITY_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        HeadlessAvalonia.Pump();
        using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No editor frame");
        frame.Save(Path.Combine(directory, "identity-password.png"), PngBitmapEncoderOptions.Default);
    }

    private sealed class Fixture : IDisposable
    {
        public const string MasterPassword = "editor test master";
        public const string OriginalPassword = "original SSH secret";
        private readonly string _path = VaultTestDb.NewPath();
        public string ConnectionString => $"Data Source={_path};Pooling=False";
        public ScriptedInteraction Interaction { get; } = new();
        public SqliteIdentityRepository Repository { get; private set; } = null!;
        public InternalVaultManager Vault { get; private set; } = null!;
        public VaultSessionService Session { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            fixture.Repository = new(fixture.ConnectionString);
            await fixture.Repository.InitializeAsync();
            fixture.Vault = VaultTestDb.CreateVault(fixture.ConnectionString);
            await fixture.Vault.SetMasterPasswordAsync(MasterPassword);
            fixture.Session = new(fixture.Vault, fixture.Vault, new FixedSettingsService(new AppSettings()), () => fixture.Interaction);
            return fixture;
        }

        public IdentityEditViewModel Editor(Identity? identity = null) => new(identity)
        {
            VaultSession = Session,
            SaveIdentityAsync = model => Repository.SaveAsync(model),
        };

        public async Task<Identity> SeedIdentityAsync()
        {
            Identity identity = new() { Name = "existing", Username = "tester", Methods = [new VaultPasswordMethod()] };
            await Repository.SaveAsync(identity);
            await Session.PersistSecretAsync(identity.Id, identity.Methods[0].Id, new SecretPayload { Password = OriginalPassword });
            return identity;
        }

        public void Dispose()
        {
            Vault.Lock();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
        }
    }
}
