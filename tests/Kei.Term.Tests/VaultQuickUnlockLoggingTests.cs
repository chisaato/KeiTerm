using System.Security.Cryptography;
using Kei.Term.App.Services;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Kei.Term.Tests;

// 快速解锁日志只允许暴露操作、阶段和错误类型；异常正文里的秘密不能进入日志。
public sealed class VaultQuickUnlockLoggingTests : IDisposable
{
    private const string MasterPassword = "quick-unlock-master";
    private const string SecretMarker = "do-not-log-7c1e-master-secret";

    private readonly string _path = VaultTestDb.NewPath();
    private readonly Guid _identity = Guid.NewGuid();
    private readonly List<IDisposable> _owned = [];
    private string Connection => $"Data Source={_path}";

    [Fact]
    public async Task Enable_WhenStoreThrows_LogsStoreStageAndRollback_WithoutExceptionText()
    {
        CaptureLogger logger = new();
        Harness harness = await OpenAsync(logger);
        InvalidOperationException? storeException = null;
        harness.Store.StoreExceptionFactory = (keyId, key) =>
            storeException = new InvalidOperationException(
                $"phase-store {SecretMarker} password={MasterPassword} keyId={keyId} key={Convert.ToBase64String(key.Span)} payload=cGF5bG9hZC1zZWNyZXQ");

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.EnableAsync(PasswordInteraction()));

        Assert.Same(storeException, thrown);
        LogCapture failure = Require(logger, "Enable", "Store", "Failed");
        Assert.Equal(LogLevel.Warning, failure.Level);
        Assert.Equal(nameof(InvalidOperationException), Value(failure, "ErrorType"));
        Assert.Null(failure.Exception);
        int failureAt = IndexOf(logger, "Enable", "Store", "Failed");
        int rollbackStart = IndexOf(logger, "Rollback", "Start", "Started");
        int rollbackDone = IndexOf(logger, "Rollback", "Complete", "Succeeded");
        Assert.True(failureAt < rollbackStart && rollbackStart < rollbackDone);
        Assert.Equal(0, harness.Store.Count);
        Assert.True(harness.Store.DeleteCount >= 1);
        AssertNoLeak(logger, SecretMarker, MasterPassword, "phase-store", "cGF5bG9hZC1zZWNyZXQ", harness.Store.LastKeyId, harness.Store.LastKeyBase64);
        Assert.DoesNotContain(logger.Entries, entry => Value(entry, "Result") == "Cancelled");
    }

    [Fact]
    public async Task Enable_WhenUserCancels_LogsCancellationDistinctFromFailure()
    {
        CaptureLogger logger = new();
        Harness harness = await OpenAsync(logger);

        bool enabled = await harness.Service.EnableAsync(new ScriptedInteraction());

        Assert.False(enabled);
        LogCapture cancelled = Require(logger, "Enable", "Prompt", "Cancelled");
        Assert.Equal(LogLevel.Information, cancelled.Level);
        Assert.DoesNotContain(logger.Entries, entry => Value(entry, "Result") == "Failed");
        Assert.DoesNotContain(logger.Entries, entry => Value(entry, "Operation") == "Rollback");
        Assert.DoesNotContain(logger.Entries, entry => Value(entry, "Stage") == "Store");
        Assert.Equal(0, harness.Store.Count);
        Assert.Equal(0, harness.Store.DeleteCount);
    }

    [Fact]
    public async Task Enable_WhenStoreCancels_LogsCancellationNotFailure_AndRollsBack()
    {
        CaptureLogger logger = new();
        Harness harness = await OpenAsync(logger);
        harness.Store.ThrowCancelledOnStore = true;
        harness.Store.CancelDetail = $"phase-cancel {SecretMarker} password={MasterPassword}";

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Service.EnableAsync(PasswordInteraction()));

        Require(logger, "Enable", "Store", "Cancelled");
        Require(logger, "Rollback", "Start", "Started");
        Require(logger, "Rollback", "Complete", "Succeeded");
        Assert.DoesNotContain(logger.Entries, entry => Value(entry, "Result") == "Failed");
        Assert.Equal(0, harness.Store.Count);
        Assert.True(harness.Store.DeleteCount >= 1);
        AssertNoLeak(logger, SecretMarker, MasterPassword, "phase-cancel", harness.Store.LastKeyId, harness.Store.LastKeyBase64);
    }

    [Fact]
    public async Task Enable_Succeeds_LogsPhaseResults_WithoutKeyMaterial()
    {
        CaptureLogger logger = new();
        Harness harness = await OpenAsync(logger);

        Assert.True(await harness.Service.EnableAsync(PasswordInteraction()));

        LogCapture capability = Require(logger, "Enable", "Capability", "Checked");
        Assert.Equal("True", Value(capability, "Supported"));
        Assert.Equal("True", Value(capability, "Available"));
        Assert.Equal("False", Value(capability, "Enabled"));
        Assert.Equal("True", Value(capability, "HasMasterPassword"));
        Require(logger, "Enable", "Start", "Started");
        Require(logger, "Enable", "Prepare", "Started");
        Require(logger, "Enable", "Prepare", "Succeeded");
        Require(logger, "Enable", "Store", "Started");
        Require(logger, "Enable", "Store", "Succeeded");
        Require(logger, "Enable", "Verify", "Succeeded");
        Require(logger, "Enable", "Complete", "Completed");
        Assert.DoesNotContain(logger.Entries, entry => Value(entry, "Result") is "Failed" or "Cancelled");
        VaultQuickUnlockState state = (await harness.Vault.GetQuickUnlockStateAsync())!;
        AssertNoLeak(logger, MasterPassword, SecretMarker, state.KeyId, harness.Store.LastKeyBase64, Convert.ToBase64String(harness.Store.CopyKey(state.KeyId)));

        logger.Clear();
        Assert.Equal(DeviceUnlockOutcome.Success, await harness.Service.TryUnlockAsync());
        Assert.Equal(new[] { "TryUnlock/Commit/Success" }, Names(logger));
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));

        logger.Clear();
        await harness.Service.DisableAsync();
        Assert.Equal(new[] { "Disable/Delete/Disabled" }, Names(logger));

        logger.Clear();
        Assert.Equal(DeviceUnlockOutcome.NotEnrolled, await harness.Service.TryUnlockAsync());
        Assert.Equal(new[] { "TryUnlock/Enrollment/NotEnrolled" }, Names(logger));

        logger.Clear();
        Assert.True(await harness.Service.EnableAsync(PasswordInteraction()));
        logger.Clear();
        harness.Store.Outcome = DeviceUnlockOutcome.Cancelled;
        Assert.Equal(DeviceUnlockOutcome.Cancelled, await harness.Service.TryUnlockAsync());
        Assert.Equal(new[] { "TryUnlock/Unlock/Cancelled" }, Names(logger));
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
        AssertNoLeak(logger, MasterPassword, state.KeyId);
    }

    [Fact]
    public async Task GetStatus_RepeatedRefresh_DoesNotWriteInformation()
    {
        CaptureLogger logger = new();
        Harness harness = await OpenAsync(logger);

        for (int i = 0; i < 6; i++)
        {
            QuickUnlockStatus status = await harness.Service.GetStatusAsync();
            Assert.True(status.IsSupported);
            Assert.True(status.IsAvailable);
            Assert.False(status.IsEnabled);
        }

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task GetStatus_WhenPlatformThrows_LogsSafeErrorType_WithoutMessage()
    {
        CaptureLogger logger = new();
        Harness harness = await OpenAsync(logger);
        harness.Store.AvailabilityException = new InvalidOperationException(
            $"phase-status {SecretMarker} password={MasterPassword} keyId=keiterm-v1-secret");

        QuickUnlockStatus status = await harness.Service.GetStatusAsync();

        Assert.True(status.IsSupported);
        Assert.False(status.IsAvailable);
        Assert.False(status.IsEnabled);
        LogCapture failure = Require(logger, "GetStatus", "Availability", "Failed");
        Assert.Equal(LogLevel.Warning, failure.Level);
        Assert.Equal(nameof(InvalidOperationException), Value(failure, "ErrorType"));
        Assert.Null(failure.Exception);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Information);
        AssertNoLeak(logger, SecretMarker, MasterPassword, "phase-status", "keiterm-v1-secret");
    }

    [Fact]
    public async Task Enable_WhenLoggerThrows_StillSurfacesStoreException()
    {
        Harness harness = await OpenAsync(new ThrowingLogger());
        InvalidOperationException storeException = new($"phase-store {SecretMarker} password={MasterPassword}");
        harness.Store.StoreExceptionFactory = (_, _) => storeException;

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.EnableAsync(PasswordInteraction()));

        Assert.Same(storeException, thrown);
        Assert.Contains(SecretMarker, thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("logger-blew-up", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Store.Count);
    }

    public void Dispose()
    {
        foreach (IDisposable owned in _owned) owned.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
    }

    private async Task<Harness> OpenAsync(ILogger logger)
    {
        FaultDeviceStore store = new();
        _owned.Add(store);
        await VaultTestDb.CreateAsync(Connection, _identity);
        InternalVaultManager vault = VaultTestDb.CreateVault(Connection, store);
        await vault.SetMasterPasswordAsync(MasterPassword);
        return new Harness(vault, store, new VaultQuickUnlockService(vault, store, logger));
    }

    private static ScriptedInteraction PasswordInteraction()
    {
        ScriptedInteraction interaction = new();
        interaction.MasterPasswords.Enqueue(MasterPassword);
        return interaction;
    }

    private static void AssertNoLeak(CaptureLogger logger, params string?[] secrets)
    {
        foreach (LogCapture entry in logger.Entries)
        {
            Assert.Null(entry.Exception);
            string blob = entry.Text + "\n" + string.Join("\n", entry.Properties.Select(pair => pair.Key + "=" + pair.Value));
            foreach (string? secret in secrets)
            {
                if (string.IsNullOrEmpty(secret)) continue;
                Assert.DoesNotContain(secret, blob, StringComparison.Ordinal);
            }
        }
    }

    private static LogCapture Require(CaptureLogger logger, string operation, string stage, string result)
    {
        int index = IndexOf(logger, operation, stage, result);
        Assert.True(index >= 0, $"缺少日志 Operation={operation} Stage={stage} Result={result}。实际：{Describe(logger)}");
        return logger.Entries[index];
    }

    private static int IndexOf(CaptureLogger logger, string operation, string stage, string result)
    {
        for (int i = 0; i < logger.Entries.Count; i++)
        {
            if (Value(logger.Entries[i], "Operation") == operation
                && Value(logger.Entries[i], "Stage") == stage
                && Value(logger.Entries[i], "Result") == result)
                return i;
        }
        return -1;
    }

    private static string? Value(LogCapture entry, string name)
        => entry.Properties.TryGetValue(name, out string? value) ? value : null;

    private static IReadOnlyList<string> Names(CaptureLogger logger)
        => logger.Entries.Select(entry => $"{Value(entry, "Operation")}/{Value(entry, "Stage")}/{Value(entry, "Result")}").ToArray();

    private static string Describe(CaptureLogger logger)
        => logger.Entries.Count == 0
            ? "(无)"
            : string.Join(" | ", logger.Entries.Select(entry => $"{entry.Level} {entry.Text}"));

    private sealed record Harness(InternalVaultManager Vault, FaultDeviceStore Store, VaultQuickUnlockService Service);

    private sealed class CaptureLogger : ILogger
    {
        public List<LogCapture> Entries { get; } = [];

        public void Clear() => Entries.Clear();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (state is System.Collections.IEnumerable items)
            {
                foreach (object? item in items)
                {
                    if (item == null) continue;
                    var keyProperty = item.GetType().GetProperty("Key");
                    var valueProperty = item.GetType().GetProperty("Value");
                    if (keyProperty == null || valueProperty == null) continue;
                    string? key = keyProperty.GetValue(item)?.ToString();
                    if (string.IsNullOrEmpty(key) || key == "{OriginalFormat}") continue;
                    properties[key] = valueProperty.GetValue(item)?.ToString();
                }
            }
            Entries.Add(new LogCapture(logLevel, formatter(state, exception), exception, properties));
        }
    }

    private sealed record LogCapture(
        LogLevel Level,
        string Text,
        Exception? Exception,
        IReadOnlyDictionary<string, string?> Properties);

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException($"logger-blew-up {SecretMarker}");
    }

    private sealed class FaultDeviceStore : IDeviceQuickUnlockStore, IDisposable
    {
        private readonly Dictionary<string, byte[]> _keys = [];

        public string DisplayName => "System unlock";
        public bool IsSupported { get; set; } = true;
        public int Count => _keys.Count;
        public int DeleteCount { get; private set; }
        public string? LastKeyId { get; private set; }
        public string? LastKeyBase64 { get; private set; }
        public Exception? AvailabilityException { get; set; }
        public bool ThrowCancelledOnStore { get; set; }
        public string? CancelDetail { get; set; }
        public Func<string, ReadOnlyMemory<byte>, Exception>? StoreExceptionFactory { get; set; }
        public DeviceUnlockOutcome Outcome { get; set; } = DeviceUnlockOutcome.Success;

        public byte[] CopyKey(string keyId) => _keys[keyId].ToArray();

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            if (AvailabilityException != null) throw AvailabilityException;
            return Task.FromResult(true);
        }

        public Task<bool> HasKeyAsync(string keyId, CancellationToken ct = default)
            => Task.FromResult(_keys.ContainsKey(keyId));

        public Task StoreKeyAsync(string keyId, ReadOnlyMemory<byte> key, CancellationToken ct = default)
        {
            LastKeyId = keyId;
            LastKeyBase64 = Convert.ToBase64String(key.Span);
            _keys[keyId] = key.ToArray();
            if (ThrowCancelledOnStore)
                throw new OperationCanceledException($"{CancelDetail} keyId={keyId} key={LastKeyBase64}");
            if (StoreExceptionFactory != null) throw StoreExceptionFactory(keyId, key);
            return Task.CompletedTask;
        }

        public Task<DeviceUnlockResult> UnlockKeyAsync(string keyId, string reason, CancellationToken ct = default)
        {
            if (Outcome != DeviceUnlockOutcome.Success) return Task.FromResult(new DeviceUnlockResult(Outcome));
            return Task.FromResult(new DeviceUnlockResult(DeviceUnlockOutcome.Success, CopyKey(keyId)));
        }

        public Task DeleteKeyAsync(string keyId, CancellationToken ct = default)
        {
            DeleteCount++;
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
