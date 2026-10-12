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
            // 状态会反复刷新。成功不写 Info，只有平台失败才记一条警告。
            LogFailure("GetStatus", "Availability", ex);
            return new(true, false, false, deviceStore.DisplayName, state != null);
        }
    }

    public async Task<bool> EnableAsync(IInteractionService interaction, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        await _operationGate.WaitAsync(ct);
        string? pendingKeyId = null;
        byte[]? pendingKey = null;
        string stage = "Start";
        try
        {
            LogEvent(LogLevel.Information, "Enable", stage, "Started");
            stage = "Capability";
            QuickUnlockStatus status = await GetStatusAsync(ct);
            LogCapability(status);
            if (!status.IsSupported || !status.IsAvailable || !status.HasMasterPassword)
                throw new InvalidOperationException(Strings.Get("VaultQuickUnlock.Unavailable"));
            if (status.IsEnabled)
            {
                LogEvent(LogLevel.Information, "Enable", stage, "AlreadyEnabled");
                return true;
            }

            stage = "Prompt";
            string? password = await interaction.PromptMasterPasswordAsync(Strings.Get("VaultQuickUnlock.EnablePrompt"));
            if (password == null)
            {
                LogEvent(LogLevel.Information, "Enable", stage, "Cancelled");
                return false;
            }
            ct.ThrowIfCancellationRequested();

            stage = "Prepare";
            LogEvent(LogLevel.Information, "Enable", stage, "Started");
            using VaultQuickUnlockMaterial material = await _quickVault!.PrepareQuickUnlockAsync(password, ct);
            LogEvent(LogLevel.Information, "Enable", stage, "Succeeded");
            pendingKeyId = material.KeyId;
            // 保留一份副本供回滚使用；using 释放后 material.Key 会被清零
            pendingKey = (byte[])material.Key.Clone();

            stage = "Store";
            LogEvent(LogLevel.Information, "Enable", stage, "Started");
            await deviceStore.StoreKeyAsync(material.KeyId, material.Key, ct);
            LogEvent(LogLevel.Information, "Enable", stage, "Succeeded");
            ct.ThrowIfCancellationRequested();

            stage = "Verify";
            VaultQuickUnlockState? current = await _quickVault.GetQuickUnlockStateAsync(ct);
            if (current?.KeyId != material.KeyId)
            {
                // 系统写入期间主密码若轮换，撤回这次旧密钥的设备登记。
                LogEvent(LogLevel.Warning, "Enable", stage, "Stale");
                await RollbackEnrollmentAsync(material.KeyId, pendingKey);
                pendingKeyId = null;
                return false;
            }
            LogEvent(LogLevel.Information, "Enable", stage, "Succeeded");
            Interlocked.Increment(ref _configurationVersion);
            pendingKeyId = null;
            stage = "Complete";
            LogEvent(LogLevel.Information, "Enable", stage, "Completed");
            return true;
        }
        catch (Exception ex)
        {
            // 先记下原始失败所在阶段，再回滚。取消与失败分开，避免把用户取消当成平台错误。
            if (ex is OperationCanceledException)
                LogEvent(LogLevel.Information, "Enable", stage, "Cancelled");
            else
                LogFailure("Enable", stage, ex);
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
        string stage = "State";
        try
        {
            VaultQuickUnlockState? state = _quickVault == null ? null : await _quickVault.GetQuickUnlockStateAsync(ct);
            if (state == null)
            {
                LogEvent(LogLevel.Information, "Disable", stage, "NotEnrolled");
                return;
            }
            stage = "Delete";
            await deviceStore.DeleteKeyAsync(state.KeyId, ct);
            LogEvent(LogLevel.Information, "Disable", stage, "Disabled");
        }
        catch (OperationCanceledException)
        {
            LogEvent(LogLevel.Information, "Disable", stage, "Cancelled");
            throw;
        }
        catch (Exception ex)
        {
            LogFailure("Disable", stage, ex);
            throw;
        }
        finally { _operationGate.Release(); }
    }

    public async Task<DeviceUnlockOutcome> TryUnlockAsync(CancellationToken ct = default)
    {
        long configurationVersion = Interlocked.Read(ref _configurationVersion);
        await _operationGate.WaitAsync(ct);
        byte[]? key = null;
        string stage = "Availability";
        try
        {
            if (_quickVault == null || !deviceStore.IsSupported || !await deviceStore.IsAvailableAsync(ct))
                return Finish("TryUnlock", stage, DeviceUnlockOutcome.Unavailable);
            stage = "Enrollment";
            VaultQuickUnlockState? state = await _quickVault.GetQuickUnlockStateAsync(ct);
            if (state == null || !await deviceStore.HasKeyAsync(state.KeyId, ct))
                return Finish("TryUnlock", stage, DeviceUnlockOutcome.NotEnrolled);

            stage = "Unlock";
            DeviceUnlockResult result = await deviceStore.UnlockKeyAsync(state.KeyId, Strings.Get("VaultQuickUnlock.AuthenticateReason"), ct);
            key = result.Key;
            ct.ThrowIfCancellationRequested();
            if (result.Outcome != DeviceUnlockOutcome.Success)
                return Finish("TryUnlock", stage, result.Outcome);
            if (key == null || configurationVersion != Interlocked.Read(ref _configurationVersion))
                return Finish("TryUnlock", stage, DeviceUnlockOutcome.Failed);
            stage = "Commit";
            // 原生成功结果只授权这次尝试；保险库还必须验证密钥、库身份与锁定版本。
            bool unlocked = await _quickVault.UnlockWithDeviceKeyAsync(state, key, ct);
            return Finish("TryUnlock", stage, unlocked ? DeviceUnlockOutcome.Success : DeviceUnlockOutcome.Failed);
        }
        catch (OperationCanceledException)
        {
            return Finish("TryUnlock", stage, DeviceUnlockOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            LogFailure("TryUnlock", stage, ex);
            return DeviceUnlockOutcome.Failed;
        }
        finally
        {
            if (key != null) CryptographicOperations.ZeroMemory(key);
            _operationGate.Release();
        }
    }

    private DeviceUnlockOutcome Finish(string operation, string stage, DeviceUnlockOutcome outcome)
    {
        if (outcome == DeviceUnlockOutcome.Cancelled)
            LogEvent(LogLevel.Information, operation, stage, "Cancelled");
        else if (outcome == DeviceUnlockOutcome.Failed)
            LogEvent(LogLevel.Warning, operation, stage, "Failed");
        else
            LogEvent(LogLevel.Information, operation, stage, outcome.ToString());
        return outcome;
    }

    // 只记录异常类型。Message / ToString 可能带上主密码、密钥或 keyId。
    private void LogFailure(string operation, string stage, Exception ex)
        => LogEvent(LogLevel.Warning, operation, stage, "Failed", ex.GetType().Name);

    private void LogCapability(QuickUnlockStatus status)
    {
        try
        {
            _logger.LogInformation(
                "本机快速解锁 Operation={Operation} Stage={Stage} Result={Result} Supported={Supported} Available={Available} Enabled={Enabled} HasMasterPassword={HasMasterPassword}",
                "Enable",
                "Capability",
                "Checked",
                status.IsSupported,
                status.IsAvailable,
                status.IsEnabled,
                status.HasMasterPassword);
        }
        catch
        {
            // 日志失败不能改变启用结果
        }
    }

    private void LogEvent(LogLevel level, string operation, string stage, string result, string? errorType = null)
    {
        try
        {
            if (errorType == null)
            {
                _logger.Log(level, "本机快速解锁 Operation={Operation} Stage={Stage} Result={Result}", operation, stage, result);
                return;
            }
            _logger.Log(
                level,
                "本机快速解锁 Operation={Operation} Stage={Stage} Result={Result} ErrorType={ErrorType}",
                operation,
                stage,
                result,
                errorType);
        }
        catch
        {
            // 日志失败不能替换或吞掉业务流程里的原始异常
        }
    }

    private async Task RollbackEnrollmentAsync(string keyId, ReadOnlyMemory<byte> key)
    {
        LogEvent(LogLevel.Information, "Rollback", "Start", "Started");
        // 交回 Vault 处理删除；删除失败时由 Vault 决定轮换或标记，不能只吞掉删除错误。
        if (_quickVault == null)
        {
            LogEvent(LogLevel.Warning, "Rollback", "Complete", "Skipped");
            return;
        }
        try
        {
            await _quickVault.DiscardDeviceKeyCopyAsync(keyId, key, CancellationToken.None);
            LogEvent(LogLevel.Information, "Rollback", "Complete", "Succeeded");
        }
        catch (Exception ex) { LogFailure("Rollback", "Complete", ex); }
    }
}
