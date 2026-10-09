using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Vault;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kei.Term.App.Services;

public sealed record QuickUnlockStatus(bool IsSupported, bool IsAvailable, bool IsEnabled, string DisplayName, bool HasMasterPassword = true);

// 将系统验证与真实 Vault 密钥校验连接起来；不启动后台自动解锁，也不保留明文主密码。
public sealed class VaultQuickUnlockService(IVaultManager vault, IDeviceQuickUnlockStore deviceStore, ILogger? logger = null)
{
    private readonly IQuickUnlockVault? _quickVault = vault as IQuickUnlockVault;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private long _configurationVersion;

    public async Task<QuickUnlockStatus> GetStatusAsync(CancellationToken ct = default)
    {
        bool supported = deviceStore.IsSupported && _quickVault != null;
        VaultQuickUnlockState? state = _quickVault == null ? null : await _quickVault.GetQuickUnlockStateAsync(ct);
        if (!supported) return new(false, false, false, deviceStore.DisplayName, state != null);

        try
        {
            bool enabled = state != null && await deviceStore.HasKeyAsync(state.KeyId, ct);
            bool available = await deviceStore.IsAvailableAsync(ct);
            return new(true, available, enabled, deviceStore.DisplayName, state != null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogPlatformFailure(ex);
            return new(true, false, false, deviceStore.DisplayName, state != null);
        }
    }

    public async Task<bool> EnableAsync(IInteractionService interaction, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        await _operationGate.WaitAsync(ct);
        string? pendingKeyId = null;
        byte[]? pendingKey = null;
        try
        {
            QuickUnlockStatus status = await GetStatusAsync(ct);
            if (!status.IsSupported || !status.IsAvailable || !status.HasMasterPassword)
                throw new InvalidOperationException(Strings.Get("VaultQuickUnlock.Unavailable"));
            if (status.IsEnabled) return true;

            string? password = await interaction.PromptMasterPasswordAsync(Strings.Get("VaultQuickUnlock.EnablePrompt"));
            if (password == null) return false;
            ct.ThrowIfCancellationRequested();
            using VaultQuickUnlockMaterial material = await _quickVault!.PrepareQuickUnlockAsync(password, ct);
            pendingKeyId = material.KeyId;
            // 保留一份副本供回滚使用；using 释放后 material.Key 会被清零
            pendingKey = (byte[])material.Key.Clone();
            await deviceStore.StoreKeyAsync(material.KeyId, material.Key, ct);
            ct.ThrowIfCancellationRequested();
            VaultQuickUnlockState? current = await _quickVault.GetQuickUnlockStateAsync(ct);
            if (current?.KeyId != material.KeyId)
            {
                // 系统写入期间主密码若轮换，撤回这次旧密钥的设备登记。
                await RollbackEnrollmentAsync(material.KeyId, pendingKey);
                pendingKeyId = null;
                return false;
            }
            Interlocked.Increment(ref _configurationVersion);
            pendingKeyId = null;
            return true;
        }
        catch
        {
            // 原生写入不能总是取消，即使 Store 抛出取消，也必须撤回可能已写入的新项。
            if (pendingKeyId != null) await RollbackEnrollmentAsync(pendingKeyId, pendingKey!);
            throw;
        }
        finally
        {
            if (pendingKey != null) CryptographicOperations.ZeroMemory(pendingKey);
            _operationGate.Release();
        }
    }

    public async Task DisableAsync(CancellationToken ct = default)
    {
        // 先撤销在飞验证的提交资格，再等待原生操作完成。
        Interlocked.Increment(ref _configurationVersion);
        _quickVault?.InvalidateQuickUnlockAttempts();
        await _operationGate.WaitAsync(ct);
        try
        {
            VaultQuickUnlockState? state = _quickVault == null ? null : await _quickVault.GetQuickUnlockStateAsync(ct);
            if (state != null) await deviceStore.DeleteKeyAsync(state.KeyId, ct);
        }
        finally { _operationGate.Release(); }
    }

    public async Task<DeviceUnlockOutcome> TryUnlockAsync(CancellationToken ct = default)
    {
        long configurationVersion = Interlocked.Read(ref _configurationVersion);
        await _operationGate.WaitAsync(ct);
        byte[]? key = null;
        try
        {
            if (_quickVault == null || !deviceStore.IsSupported || !await deviceStore.IsAvailableAsync(ct))
                return DeviceUnlockOutcome.Unavailable;
            VaultQuickUnlockState? state = await _quickVault.GetQuickUnlockStateAsync(ct);
            if (state == null || !await deviceStore.HasKeyAsync(state.KeyId, ct)) return DeviceUnlockOutcome.NotEnrolled;

            DeviceUnlockResult result = await deviceStore.UnlockKeyAsync(state.KeyId, Strings.Get("VaultQuickUnlock.AuthenticateReason"), ct);
            key = result.Key;
            ct.ThrowIfCancellationRequested();
            if (result.Outcome != DeviceUnlockOutcome.Success) return result.Outcome;
            if (key == null || configurationVersion != Interlocked.Read(ref _configurationVersion)) return DeviceUnlockOutcome.Failed;
            // 原生成功结果只授权这次尝试；保险库还必须验证密钥、库身份与锁定版本。
            return await _quickVault.UnlockWithDeviceKeyAsync(state, key, ct)
                ? DeviceUnlockOutcome.Success : DeviceUnlockOutcome.Failed;
        }
        catch (OperationCanceledException) { return DeviceUnlockOutcome.Cancelled; }
        catch (Exception ex)
        {
            LogPlatformFailure(ex);
            return DeviceUnlockOutcome.Failed;
        }
        finally
        {
            if (key != null) CryptographicOperations.ZeroMemory(key);
            _operationGate.Release();
        }
    }

    private void LogPlatformFailure(Exception ex)
        => _logger.LogWarning("本机快速解锁不可用，错误类型={ErrorType}", ex.GetType().Name);

    private async Task RollbackEnrollmentAsync(string keyId, ReadOnlyMemory<byte> key)
    {
        // 交回 Vault 处理删除；删除失败时由 Vault 决定轮换或标记，不能只吞掉删除错误。
        if (_quickVault == null) return;
        try { await _quickVault.DiscardDeviceKeyCopyAsync(keyId, key, CancellationToken.None); }
        catch (Exception ex) { LogPlatformFailure(ex); }
    }
}
