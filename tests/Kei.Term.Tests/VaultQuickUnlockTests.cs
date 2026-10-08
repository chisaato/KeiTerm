using System.Security.Cryptography;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Vault;
using Microsoft.Data.Sqlite;

namespace Kei.Term.Tests;

// 仅替换系统认证边界；保险库加密、持久化、主密码校验和会话交互均运行生产代码。
public sealed class VaultQuickUnlockTests : IDisposable
{
    private readonly string _path = VaultTestDb.NewPath();
    private readonly Guid _identity = Guid.NewGuid();
    private readonly MemoryDeviceStore _store = new();
    private string Connection => $"Data Source={_path}";
    private const string MasterPassword = "quick-unlock-master";

    private async Task<InternalVaultManager> CreateAsync(bool enroll = true)
    {
        await VaultTestDb.CreateAsync(Connection, _identity);
        InternalVaultManager vault = new(Connection, quickUnlockStore: _store);
        await vault.SetMasterPasswordAsync(MasterPassword);
        await vault.SaveSecretsAsync(_identity, new() { ["method"] = new SecretPayload { PrivateKeyContent = "private-key", Passphrase = "passphrase" } });
        if (enroll) Assert.True(await new VaultQuickUnlockService(vault, _store).EnableAsync(PasswordInteraction()));
        vault.Lock();
        return vault;
    }

    private static ScriptedInteraction PasswordInteraction(string password = MasterPassword)
    {
        ScriptedInteraction interaction = new();
        interaction.MasterPasswords.Enqueue(password);
        return interaction;
    }

    [Fact]
    public async Task ReopenedVault_UnlocksRealEncryptedSecrets_AndErasesReturnedKey()
    {
        await CreateAsync();
        InternalVaultManager reopened = new(Connection, quickUnlockStore: _store);
        Assert.False(await reopened.TryAutoUnlockAsync());
        VaultQuickUnlockService service = new(reopened, _store);

        Assert.Equal(DeviceUnlockOutcome.Success, await service.TryUnlockAsync());
        Dictionary<string, SecretPayload> secrets = await reopened.GetSecretsAsync(_identity);
        Assert.Equal("private-key", secrets["method"].PrivateKeyContent);
        Assert.Equal("passphrase", secrets["method"].Passphrase);
        Assert.NotNull(_store.LastReturnedKey);
        Assert.All(_store.LastReturnedKey, value => Assert.Equal(0, value));
        var raw = await VaultTestDb.ReadRawAsync(Connection, _identity);
        Assert.Equal("AES-256-GCM", raw.algorithm);
        Assert.DoesNotContain("private-key", System.Text.Encoding.UTF8.GetString(raw.blob));
    }

    [Fact]
    public async Task Enrollment_RequiresPasswordProof_EvenWhenAlreadyUnlocked()
    {
        InternalVaultManager vault = await CreateAsync(enroll: false);
        await vault.UnlockAsync(MasterPassword, false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new VaultQuickUnlockService(vault, _store).EnableAsync(PasswordInteraction("wrong")));
        Assert.Equal(0, _store.Count);
        Assert.Equal("private-key", (await vault.GetSecretsAsync(_identity))["method"].PrivateKeyContent);
    }

    [Fact]
    public async Task CorruptedDeviceKey_CannotUnlock_AndIsErased()
    {
        InternalVaultManager vault = await CreateAsync();
        _store.CorruptReturnedKey = true;
        Assert.Equal(DeviceUnlockOutcome.Failed, await new VaultQuickUnlockService(vault, _store).TryUnlockAsync());
        Assert.False(vault.IsUnlocked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.GetSecretsAsync(_identity));
        Assert.All(_store.LastReturnedKey!, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task KeyFromAnotherVault_AndWrongLength_CannotUnlock()
    {
        InternalVaultManager vault = await CreateAsync();
        VaultQuickUnlockState state = (await vault.GetQuickUnlockStateAsync())!;
        string otherPath = VaultTestDb.NewPath();
        byte[]? copiedKey = null;
        try
        {
            string otherConnection = $"Data Source={otherPath}";
            await VaultTestDb.CreateAsync(otherConnection, Guid.NewGuid());
            InternalVaultManager other = new(otherConnection);
            await other.SetMasterPasswordAsync(MasterPassword);
            using VaultQuickUnlockMaterial material = await other.PrepareQuickUnlockAsync(MasterPassword);
            copiedKey = material.Key.ToArray();
            Assert.False(await vault.UnlockWithDeviceKeyAsync(state, material.Key));
            Assert.False(await vault.UnlockWithDeviceKeyAsync(state, new byte[31]));
            Assert.False(vault.IsUnlocked);
        }
        finally
        {
            if (copiedKey != null) CryptographicOperations.ZeroMemory(copiedKey);
            DeleteDatabase(otherPath);
        }
    }

    [Fact]
    public async Task PasswordRotation_RevokesOldRegistration_AndPreservesSecrets()
    {
        InternalVaultManager vault = await CreateAsync();
        VaultQuickUnlockState before = (await vault.GetQuickUnlockStateAsync())!;
        byte[] oldKey = _store.CopyKey(before.KeyId);
        try
        {
            await vault.UnlockAsync(MasterPassword, false);
            await vault.SetMasterPasswordAsync("new-master");
            VaultQuickUnlockState after = (await vault.GetQuickUnlockStateAsync())!;
            Assert.NotEqual(before.KeyId, after.KeyId);
            Assert.Equal(0, _store.Count);
            vault.Lock();
            after = (await vault.GetQuickUnlockStateAsync())!;
            Assert.False(await vault.UnlockWithDeviceKeyAsync(after, oldKey));
            Assert.Equal(DeviceUnlockOutcome.NotEnrolled, await new VaultQuickUnlockService(vault, _store).TryUnlockAsync());
            await vault.UnlockAsync("new-master", false);
            Assert.Equal("passphrase", (await vault.GetSecretsAsync(_identity))["method"].Passphrase);
        }
        finally { CryptographicOperations.ZeroMemory(oldKey); }
    }

    [Fact]
    public async Task CancelAfterUncancellableStore_RollsBackRegistration()
    {
        InternalVaultManager vault = await CreateAsync(enroll: false);
        using CancellationTokenSource cancellation = new();
        _store.AfterStore = cancellation.Cancel;
        VaultQuickUnlockService service = new(vault, _store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnableAsync(PasswordInteraction(), cancellation.Token));
        Assert.Equal(0, _store.Count);
        Assert.False((await service.GetStatusAsync()).IsEnabled);
        vault.Lock();
        Assert.Equal(DeviceUnlockOutcome.NotEnrolled, await service.TryUnlockAsync());
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task LockDuringSystemVerification_RejectsLateSuccess()
    {
        InternalVaultManager vault = await CreateAsync();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.BeforeUnlock = async () => { started.SetResult(); await resume.Task; };
        Task<DeviceUnlockOutcome> attempt = new VaultQuickUnlockService(vault, _store).TryUnlockAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vault.Lock();
        resume.SetResult();
        Assert.Equal(DeviceUnlockOutcome.Failed, await attempt);
        Assert.False(vault.IsUnlocked);
        Assert.All(_store.LastReturnedKey!, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task DisableAfterSystemSuccess_BeforeVaultCommit_RejectsLateUnlock()
    {
        InternalVaultManager vault = await CreateAsync();
        DelayedVault delayed = new(vault);
        VaultQuickUnlockService service = new(delayed, _store);
        Task<DeviceUnlockOutcome> attempt = service.TryUnlockAsync();
        await delayed.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task disable = service.DisableAsync();
        delayed.ResumeCommit.SetResult();
        Assert.Equal(DeviceUnlockOutcome.Failed, await attempt);
        await disable;
        Assert.False(vault.IsUnlocked);
        Assert.Equal(0, _store.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.GetSecretsAsync(_identity));
    }

    [Fact]
    public async Task CancelDuringNativeSuccess_DoesNotCommitKey()
    {
        InternalVaultManager vault = await CreateAsync();
        using CancellationTokenSource cancellation = new();
        _store.BeforeUnlock = () => { cancellation.Cancel(); return Task.CompletedTask; };
        Assert.Equal(DeviceUnlockOutcome.Cancelled, await new VaultQuickUnlockService(vault, _store).TryUnlockAsync(cancellation.Token));
        Assert.False(vault.IsUnlocked);
        Assert.All(_store.LastReturnedKey!, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(DeviceUnlockOutcome.Cancelled)]
    [InlineData(DeviceUnlockOutcome.Failed)]
    [InlineData(DeviceUnlockOutcome.Unavailable)]
    public async Task SystemFailure_ReturnsToPassword_WithoutAutomaticBiometricRetry(DeviceUnlockOutcome outcome)
    {
        InternalVaultManager vault = await CreateAsync();
        _store.Outcome = outcome;
        ScriptedInteraction interaction = new();
        interaction.VaultResponses.Enqueue(new(VaultUnlockKind.QuickUnlock));
        interaction.VaultResponses.Enqueue(new(VaultUnlockKind.Password, MasterPassword));
        VaultSessionService session = new(vault, vault, new FixedSettingsService(), () => interaction);
        session.ConfigureQuickUnlock(new(vault, _store));

        Dictionary<string, SecretPayload> secrets = await session.LoadIdentitySecretsAsync(_identity);
        Assert.Equal("private-key", secrets["method"].PrivateKeyContent);
        Assert.Equal(1, _store.UnlockCount);
        Assert.Equal(2, interaction.VaultPrompts.Count);
        Assert.True(interaction.VaultPrompts[0].CanQuickUnlock);
        Assert.Null(interaction.VaultPrompts[0].Error);
        VaultUnlockPrompt retry = interaction.VaultPrompts[1];
        if (outcome == DeviceUnlockOutcome.Failed)
        {
            Assert.NotNull(retry.Error);
            Assert.Null(retry.Notice);
        }
        else
        {
            Assert.Null(retry.Error);
            Assert.NotNull(retry.Notice);
        }
    }

    [Fact]
    public async Task SystemCancel_ThenDialogCancel_KeepsLocked_AndDoesNotPromptInBackground()
    {
        InternalVaultManager vault = await CreateAsync();
        _store.Outcome = DeviceUnlockOutcome.Cancelled;
        ScriptedInteraction interaction = new();
        interaction.VaultResponses.Enqueue(new(VaultUnlockKind.QuickUnlock));
        interaction.VaultResponses.Enqueue(new(VaultUnlockKind.Cancelled));
        VaultSessionService session = new(vault, vault, new FixedSettingsService(), () => interaction);
        session.ConfigureQuickUnlock(new(vault, _store));
        Assert.False(await session.EnsureUnlockedAsync());
        Assert.False(vault.IsUnlocked);
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.LoadIdentitySecretsAsync(_identity, allowInteraction: false));
        Assert.Equal(1, _store.UnlockCount);
        Assert.Equal(2, interaction.VaultPrompts.Count);
    }

    [Theory]
    [InlineData(VaultUnlockKind.Password, true)]
    [InlineData(VaultUnlockKind.Cancelled, false)]
    public async Task ConcurrentConnections_ShareOnePromptAndResult(VaultUnlockKind kind, bool expected)
    {
        InternalVaultManager vault = await CreateAsync();
        TaskCompletionSource<VaultUnlockResponse> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedInteraction interaction = new() { VaultPromptHandler = _ => response.Task };
        VaultSessionService session = new(vault, vault, new FixedSettingsService(), () => interaction);
        session.ConfigureQuickUnlock(new(vault, _store));
        Task<bool> first = session.EnsureUnlockedAsync();
        Task<bool> second = session.EnsureUnlockedAsync();
        response.SetResult(new(kind, MasterPassword));
        Assert.Equal(expected, await first);
        Assert.Equal(expected, await second);
        Assert.Single(interaction.VaultPrompts);
        Assert.Equal(expected, vault.IsUnlocked);
    }

    public void Dispose()
    {
        _store.Dispose();
        DeleteDatabase(_path);
    }

    private static void DeleteDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
    }

    private sealed class MemoryDeviceStore : IDeviceQuickUnlockStore, IDisposable
    {
        private readonly Dictionary<string, byte[]> _keys = [];
        public string DisplayName => "System unlock";
        public bool IsSupported => true;
        public int Count => _keys.Count;
        public int UnlockCount { get; private set; }
        public bool CorruptReturnedKey { get; set; }
        public byte[]? LastReturnedKey { get; private set; }
        public Action? AfterStore { get; set; }
        public Func<Task>? BeforeUnlock { get; set; }
        public DeviceUnlockOutcome Outcome { get; set; } = DeviceUnlockOutcome.Success;
        public byte[] CopyKey(string keyId) => _keys[keyId].ToArray();
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> HasKeyAsync(string keyId, CancellationToken ct = default) => Task.FromResult(_keys.ContainsKey(keyId));
        public Task StoreKeyAsync(string keyId, ReadOnlyMemory<byte> key, CancellationToken ct = default)
        {
            _keys[keyId] = key.ToArray();
            AfterStore?.Invoke();
            return Task.CompletedTask;
        }
        public async Task<DeviceUnlockResult> UnlockKeyAsync(string keyId, string reason, CancellationToken ct = default)
        {
            UnlockCount++;
            if (BeforeUnlock != null) await BeforeUnlock();
            if (Outcome != DeviceUnlockOutcome.Success) return new(Outcome);
            LastReturnedKey = CopyKey(keyId);
            if (CorruptReturnedKey) LastReturnedKey[0] ^= 0x80;
            return new(DeviceUnlockOutcome.Success, LastReturnedKey);
        }
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

    // 把原生成功与真实保险库提交分开调度，确定性覆盖停用发生于数据库等待期间的竞态。
    private sealed class DelayedVault(InternalVaultManager inner) : IVaultManager, IQuickUnlockVault
    {
        public TaskCompletionSource CommitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsUnlocked => inner.IsUnlocked;
        public bool IsPlainMode => inner.IsPlainMode;
        public void Lock() => inner.Lock();
        public void InvalidateQuickUnlockAttempts() => inner.InvalidateQuickUnlockAttempts();
        public Task SetMasterPasswordAsync(string password, CancellationToken ct = default) => inner.SetMasterPasswordAsync(password, ct);
        public Task<bool> TryAutoUnlockAsync(CancellationToken ct = default) => inner.TryAutoUnlockAsync(ct);
        public Task UnlockAsync(string password, bool rememberOnThisDevice, CancellationToken ct = default) => inner.UnlockAsync(password, rememberOnThisDevice, ct);
        public Task<VaultQuickUnlockState?> GetQuickUnlockStateAsync(CancellationToken ct = default) => inner.GetQuickUnlockStateAsync(ct);
        public Task<VaultQuickUnlockMaterial> PrepareQuickUnlockAsync(string password, CancellationToken ct = default) => inner.PrepareQuickUnlockAsync(password, ct);
        public async Task<bool> UnlockWithDeviceKeyAsync(VaultQuickUnlockState state, ReadOnlyMemory<byte> key, CancellationToken ct = default)
        {
            CommitStarted.SetResult();
            await ResumeCommit.Task;
            return await inner.UnlockWithDeviceKeyAsync(state, key, ct);
        }
    }
}
