using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Kei.Term.App.Helpers;
using Kei.Term.App.Models;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Vault;
using Kei.Term.Ssh.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kei.Term.App.Services;

// 本次运行内的 Vault 会话：懒解锁、身份材料读写、SessionOnly 口令缓存、空闲自动锁定判定。
// 不含计时器（由 UI 层按分钟调用 AutoLockIfIdle），因此可在无界面环境下单测。
public sealed class VaultSessionService
{
    private readonly IVaultManager _vault;
    private readonly IVaultSecretStore _secretStore;
    private readonly ISettingsService _settings;
    private readonly Func<IInteractionService> _interaction;
    private readonly ILogger _logger;
    private VaultQuickUnlockService? _quickUnlock;
    private readonly object _unlockCoordinationLock = new();
    private Task<bool>? _pendingUnlock;

    // SessionOnly 口令缓存：键 = 方法 Id；锁定时清空
    private readonly Dictionary<Guid, string> _sessionPassphrases = new();

    // 连续主密码错误次数（成功即清零），仅用于日志
    private int _unlockFailures;

    public VaultSessionService(
        IVaultManager vault,
        IVaultSecretStore secretStore,
        ISettingsService settings,
        Func<IInteractionService> interaction,
        ILogger? logger = null)
    {
        _vault = vault;
        _secretStore = secretStore;
        _settings = settings;
        _interaction = interaction;
        _logger = logger ?? NullLogger.Instance;
    }

    // 最近一次 Vault 访问时刻（UTC），空闲自动锁定以此为起点
    public DateTime LastAccessUtc { get; private set; } = DateTime.UtcNow;

    public void MarkAccessed(DateTime? nowUtc = null) => LastAccessUtc = nowUtc ?? DateTime.UtcNow;

    public void ConfigureQuickUnlock(VaultQuickUnlockService service) => _quickUnlock = service;

    // 锁定 Vault 并清空 SessionOnly 口令缓存（手动锁定与超时锁定共用）
    public void Lock()
    {
        _vault.Lock();
        _sessionPassphrases.Clear();
        MarkAccessed();
        WeakReferenceMessenger.Default.Send(new VaultLockStateChangedMessage(_vault));
        _logger.LogInformation("Vault 已锁定（内存 MEK 与 SessionOnly 口令缓存已清空）");
    }

    // 空闲超过设置阈值则锁定；返回是否执行了锁定。阈值 0 = 不自动锁定
    public bool AutoLockIfIdle(DateTime nowUtc)
    {
        int minutes = _settings.Current.LockTimeoutMinutes;
        if (minutes <= 0 || _vault.IsPlainMode || !_vault.IsUnlocked)
        {
            return false;
        }

        if (nowUtc - LastAccessUtc < TimeSpan.FromMinutes(minutes))
        {
            return false;
        }

        _logger.LogInformation("Vault 自动锁定计时触发 空闲阈值={Minutes} 分钟", minutes);
        Lock();
        return true;
    }

    public string? GetSessionPassphrase(Guid methodId)
        => _sessionPassphrases.TryGetValue(methodId, out string? cached) ? cached : null;

    // 口令弹窗（三态）；SessionOnly 勾选记住时写入本次运行内存缓存
    public async Task<PassphrasePromptResult?> PromptPassphraseAsync(FilePrivateKeyMethod method)
    {
        _logger.LogInformation("口令框打开 方法Id={MethodId} 模式={Mode}", method.Id, method.PassphraseMode);
        PassphrasePromptResult? result = await _interaction().PromptPassphraseAsync(method);
        if (result == null)
        {
            _logger.LogInformation("口令框取消 方法Id={MethodId}", method.Id);
            return null;
        }

        _logger.LogInformation("口令框确认 方法Id={MethodId} 本次运行记住={Remember}", method.Id, result.Remember);
        if (method.PassphraseMode == PassphrasePersistence.SessionOnly && result.Remember)
        {
            _sessionPassphrases[method.Id] = result.Passphrase;
        }

        return result;
    }

    // 主密码懒解锁：循环重试直到成功 / 取消
    public Task<bool> EnsureUnlockedAsync()
    {
        lock (_unlockCoordinationLock)
        {
            if (_vault.IsPlainMode || _vault.IsUnlocked) return Task.FromResult(true);
            // 多个连接共享一次用户交互；取消结果也会同时返回各调用方。
            if (_pendingUnlock is { IsCompleted: false }) return _pendingUnlock;
            _pendingUnlock = EnsureUnlockedCoreAsync();
            return _pendingUnlock;
        }
    }

    private async Task<bool> EnsureUnlockedCoreAsync()
    {
        if (_vault.IsPlainMode || _vault.IsUnlocked)
        {
            return true;
        }

        // 初次打开是正常输入提示，只有失败后才给窗口传递错误。
        string? error = null;
        string? notice = null;
        _logger.LogInformation("保管库已锁定，弹出主密码框等待解锁");
        while (true)
        {
            QuickUnlockStatus? status = null;
            if (_quickUnlock != null)
            {
                try { status = await _quickUnlock.GetStatusAsync(); }
                catch (Exception ex)
                {
                    _logger.LogWarning("无法获取快速解锁状态，错误类型={ErrorType}", ex.GetType().Name);
                }
            }
            VaultUnlockResponse response = await _interaction().PromptVaultUnlockAsync(new VaultUnlockPrompt(
                error,
                status is { IsAvailable: true, IsEnabled: true },
                status?.DisplayName,
                notice));
            if (response.Kind == VaultUnlockKind.Cancelled)
            {
                _logger.LogInformation("主密码框取消，保管库保持锁定");
                return false;
            }

            if (response.Kind == VaultUnlockKind.QuickUnlock)
            {
                DeviceUnlockOutcome outcome = _quickUnlock == null
                    ? DeviceUnlockOutcome.Unavailable : await _quickUnlock.TryUnlockAsync();
                if (outcome == DeviceUnlockOutcome.Success)
                {
                    MarkAccessed();
                    _unlockFailures = 0;
                    WeakReferenceMessenger.Default.Send(new VaultLockStateChangedMessage(_vault));
                    return true;
                }
                string message = Strings.Get(outcome switch
                {
                    DeviceUnlockOutcome.Cancelled => "VaultQuickUnlock.Cancelled",
                    DeviceUnlockOutcome.Unavailable or DeviceUnlockOutcome.NotEnrolled => "VaultQuickUnlock.Unavailable",
                    _ => "VaultQuickUnlock.Failed"
                });
                // 主动取消或系统能力不可用是普通说明，不当作密码错误标红。
                bool isNotice = outcome is DeviceUnlockOutcome.Cancelled or DeviceUnlockOutcome.Unavailable or DeviceUnlockOutcome.NotEnrolled;
                error = isNotice ? null : message;
                notice = isNotice ? message : null;
                // 返回密码窗口供用户显式重试或输入主密码，不自动重复系统弹窗。
                continue;
            }

            try
            {
                await _vault.UnlockAsync(response.Password ?? string.Empty, false);
                MarkAccessed();
                _unlockFailures = 0;
                WeakReferenceMessenger.Default.Send(new VaultLockStateChangedMessage(_vault));
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                _unlockFailures++;
                _logger.LogWarning("Vault 解锁失败：主密码错误 连续失败={Failures} 次", _unlockFailures);
                error = Strings.Get("Status.Vault.PasswordIncorrect");
                notice = null;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Vault 解锁异常");
                error = ex.Message;
                notice = null;
            }
        }
    }

    // 写入前确保可用：明文模式直通（明文警示由身份管理器横幅承担），加密模式需先解锁
    private async Task<bool> EnsureReadyForWriteAsync()
    {
        if (_vault.IsPlainMode)
        {
            _logger.LogInformation("保管库为明文模式，凭据材料将明文写入本地数据库");
            return true;
        }

        return await EnsureUnlockedAsync();
    }

    // 读取身份整包材料；取消解锁必须中止调用方，不能伪装成空材料后继续读取文件密钥。
    public async Task<Dictionary<string, SecretPayload>> LoadIdentitySecretsAsync(Guid identityId, bool allowInteraction = true)
    {
        // 自动重连只能使用当前已解锁的材料，不能在后台弹出主密码框。
        if (!allowInteraction && !_vault.IsUnlocked)
        {
            throw new OperationCanceledException("保管库已锁定，自动认证已停止。");
        }

        if (allowInteraction && !await EnsureUnlockedAsync())
        {
            throw new OperationCanceledException("已取消保管库解锁。");
        }

        try
        {
            Dictionary<string, SecretPayload> secrets = await _secretStore.GetSecretsAsync(identityId);
            MarkAccessed();
            return secrets;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取身份材料失败 身份Id={IdentityId}", identityId);
            return new Dictionary<string, SecretPayload>();
        }
    }

    // 把单个方法的材料并入身份整包写回；取消解锁同时中止本次认证材料收集。
    public async Task PersistSecretAsync(Guid identityId, Guid methodId, SecretPayload payload)
    {
        if (!await EnsureReadyForWriteAsync())
        {
            throw new OperationCanceledException("已取消保管库解锁。");
        }

        Dictionary<string, SecretPayload> secrets = await _secretStore.GetSecretsAsync(identityId);
        secrets[methodId.ToString()] = payload;
        await _secretStore.SaveSecretsAsync(identityId, secrets);
        MarkAccessed();
        // 仅记录有无与字节数，绝不记录材料内容
        _logger.LogInformation(
            "Vault 材料持久化 身份Id={IdentityId} 方法Id={MethodId} 含密码={HasPassword} 私钥字节={KeyBytes} 含口令={HasPassphrase}",
            identityId,
            methodId,
            !string.IsNullOrEmpty(payload.Password),
            payload.PrivateKeyContent == null ? 0 : PrivateKeyImport.ByteCount(payload.PrivateKeyContent),
            !string.IsNullOrEmpty(payload.Passphrase));
    }

    // 指定 Vault 私钥方法的已存材料信息（字节数 + 指纹）；无材料返回 null。供身份编辑器回显
    public async Task<VaultKeyInfo?> GetVaultKeyInfoAsync(Guid identityId, Guid methodId)
    {
        Dictionary<string, SecretPayload> secrets;
        try
        {
            secrets = await LoadIdentitySecretsAsync(identityId);
        }
        catch (OperationCanceledException)
        {
            // 编辑器的信息预览无需建立连接；取消时保留空预览。
            return null;
        }

        if (!secrets.TryGetValue(methodId.ToString(), out SecretPayload? payload)
            || string.IsNullOrEmpty(payload.PrivateKeyContent))
        {
            return null;
        }

        return new VaultKeyInfo(
            PrivateKeyImport.ByteCount(payload.PrivateKeyContent),
            SshKeyFingerprint.Compute(payload.PrivateKeyContent, payload.Passphrase));
    }

    // 身份落库后统一写入/删除其 Vault 私钥材料；返回 false 表示保管库未解锁（用户取消）
    public async Task<bool> PersistVaultKeyImportsAsync(Guid identityId, IReadOnlyList<VaultKeyImport> imports)
    {
        if (imports.Count == 0)
        {
            return true;
        }

        if (!await EnsureReadyForWriteAsync())
        {
            return false;
        }

        Dictionary<string, SecretPayload> secrets = await _secretStore.GetSecretsAsync(identityId);
        foreach (VaultKeyImport import in imports)
        {
            string key = import.MethodId.ToString();
            if (import.Remove)
            {
                secrets.Remove(key);
                _logger.LogInformation("Vault 私钥材料移除 身份Id={IdentityId} 方法Id={MethodId}", identityId, import.MethodId);
                continue;
            }

            if (!string.IsNullOrEmpty(import.PrivateKeyContent))
            {
                secrets[key] = new SecretPayload
                {
                    PrivateKeyContent = import.PrivateKeyContent,
                    Passphrase = import.Passphrase,
                };
                // 记录字节数与指纹有无，不记录私钥内容
                bool hasFingerprint = SshKeyFingerprint.Compute(import.PrivateKeyContent, import.Passphrase) != null;
                _logger.LogInformation(
                    "Vault 私钥材料写入 身份Id={IdentityId} 方法Id={MethodId} 字节={Bytes} 指纹={HasFingerprint}",
                    identityId,
                    import.MethodId,
                    PrivateKeyImport.ByteCount(import.PrivateKeyContent),
                    hasFingerprint);
            }
        }

        await _secretStore.SaveSecretsAsync(identityId, secrets);
        MarkAccessed();
        return true;
    }
}
