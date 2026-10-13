using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
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
    private readonly IVaultSecretLibrary _library;
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
        IVaultSecretLibrary library,
        ISettingsService settings,
        Func<IInteractionService> interaction,
        ILogger? logger = null)
    {
        _vault = vault;
        _library = library;
        _settings = settings;
        _interaction = interaction;
        _logger = logger ?? NullLogger.Instance;
    }

    // 最近一次 Vault 访问时刻（UTC），空闲自动锁定以此为起点
    public DateTime LastAccessUtc { get; private set; } = DateTime.UtcNow;

    public void MarkAccessed(DateTime? nowUtc = null) => LastAccessUtc = nowUtc ?? DateTime.UtcNow;

    public void ConfigureQuickUnlock(VaultQuickUnlockService service) => _quickUnlock = service;

    // 当前是否已解锁（明文模式恒为 true）；供材料库等只读路径判断能否补算元数据
    public bool IsUnlocked => _vault.IsUnlocked;

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
    public async Task<bool> EnsureReadyForWriteAsync()
    {
        if (_vault.IsPlainMode)
        {
            _logger.LogInformation("保管库为明文模式，凭据材料将明文写入本地数据库");
            return true;
        }

        return await EnsureUnlockedAsync();
    }

    // 材料库元数据列表：不解锁也能列出与选择
    public Task<IReadOnlyList<VaultSecret>> ListLibrarySecretsAsync(CancellationToken ct = default)
        => _library.ListAsync(ct);

    // 使用已存身份材料前的读门槛（与旧身份整包读取一致）：
    // allowInteraction=false（自动重连）锁定时中止；否则先解锁，取消解锁同样中止。
    public async Task EnsureReadyForReadAsync(bool allowInteraction, CancellationToken ct = default)
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
    }

    // 取单条材料载荷：需解锁。取消解锁必须中止调用方，不能伪装成空材料后继续读取文件密钥。
    public async Task<SecretPayload?> GetSecretPayloadAsync(Guid secretId, bool allowInteraction = true, CancellationToken ct = default)
    {
        await EnsureReadyForReadAsync(allowInteraction, ct);

        try
        {
            SecretPayload? payload = await _library.GetPayloadAsync(secretId, ct);
            MarkAccessed();
            if (payload == null)
            {
                // 条目不存在/未命中属正常结果，仅在排查时可见：降到 Debug，且只记条目 Id
                _logger.LogDebug("材料库条目不存在 条目Id={SecretId}", secretId);
            }

            return payload;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CryptographicException ex)
        {
            // 预期错误（密文损坏/AAD 不匹配）：只记条目 Id 与异常类型，不打印堆栈
            _logger.LogWarning("读取材料库条目失败 条目Id={SecretId} 错误类型={ErrorType}", secretId, ex.GetType().Name);
            return null;
        }
        catch (Exception ex)
        {
            // 非预期错误：保留完整异常便于定位，但日志正文仍不含任何材料内容
            _logger.LogWarning(ex, "读取材料库条目失败 条目Id={SecretId} 错误类型={ErrorType}", secretId, ex.GetType().Name);
            return null;
        }
    }

    // 新建/更新材料条目；私钥载荷写入时计算并落库指纹与密钥类型。false = 保管库未解锁（取消）
    public async Task<bool> SaveLibrarySecretAsync(VaultSecret secret, SecretPayload? payload, CancellationToken ct = default)
    {
        if (!await EnsureReadyForWriteAsync())
        {
            return false;
        }

        VaultSecret toSave = EnrichMetadata(secret, payload);
        await _library.SaveAsync(toSave, payload, ct);
        MarkAccessed();
        // 仅记录有无与字节数，绝不记录材料内容
        _logger.LogInformation(
            "材料库条目写入 条目Id={SecretId} 类型={Kind} 含密码={HasPassword} 私钥字节={KeyBytes} 含口令={HasPassphrase} 指纹={HasFingerprint}",
            toSave.Id,
            toSave.Kind,
            !string.IsNullOrEmpty(payload?.Password),
            payload?.PrivateKeyContent == null ? 0 : PrivateKeyImport.ByteCount(payload.PrivateKeyContent),
            !string.IsNullOrEmpty(payload?.Passphrase),
            !string.IsNullOrEmpty(toSave.Fingerprint));
        return true;
    }

    // 删除材料条目（引用校验由调用方负责）。false = 保管库未解锁（取消）
    public async Task<bool> DeleteLibrarySecretAsync(Guid secretId, CancellationToken ct = default)
    {
        if (!await EnsureReadyForWriteAsync())
        {
            return false;
        }

        await _library.DeleteAsync(secretId, ct);
        MarkAccessed();
        _logger.LogInformation("材料库条目删除 条目Id={SecretId}", secretId);
        return true;
    }

    // 惰性补算缺失的私钥指纹/密钥类型（迁移产出的旧条目 meta_json 为空）。
    // 仅在已解锁（或明文模式）时执行：锁定时直接返回，不读取载荷、不弹主密码框。
    // 并发最小缓解：以库内最新元数据为基准，并只写元数据（payload = null，复用已存密文封套），
    // 避免用调用方可能已过期的快照覆盖并发的改名，也绝不重加密或触碰载荷。
    public async Task BackfillLibraryMetadataAsync(IReadOnlyList<VaultSecret> secrets, CancellationToken ct = default)
    {
        if (!_vault.IsUnlocked)
        {
            return;
        }

        IReadOnlyList<VaultSecret> latest = await _library.ListAsync(ct);
        var latestById = latest.ToDictionary(secret => secret.Id);

        foreach (VaultSecret snapshot in secrets)
        {
            // 条目已被删除：跳过（悬空引用在消费端另行按可恢复处理）
            if (!latestById.TryGetValue(snapshot.Id, out VaultSecret? current))
            {
                continue;
            }

            if (current.Kind != SecretKind.SshPrivateKey
                || (!string.IsNullOrEmpty(current.Fingerprint) && !string.IsNullOrEmpty(current.KeyType)))
            {
                continue;
            }

            SecretPayload? payload;
            try
            {
                payload = await _library.GetPayloadAsync(current.Id, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单条读取失败不影响其余条目，也不阻断列表展示
                _logger.LogWarning("材料库元数据补算读取失败 条目Id={SecretId} 错误类型={ErrorType}", current.Id, ex.GetType().Name);
                continue;
            }

            if (string.IsNullOrEmpty(payload?.PrivateKeyContent))
            {
                continue;
            }

            VaultSecret enriched = EnrichMetadata(current, payload);
            if (ReferenceEquals(enriched, current))
            {
                continue;
            }

            // 只更新元数据：SaveAsync(secret, null) 复用已存封套，不重新加密、不触碰载荷
            await _library.SaveAsync(enriched, null, ct);
            MarkAccessed();
            _logger.LogInformation("材料库元数据补算 条目Id={SecretId} 类型={Kind}", current.Id, current.Kind);
        }
    }

    // 私钥载荷：写入前在 App 层解析出指纹与密钥类型一并落库；口令条目无需指纹。
    private static VaultSecret EnrichMetadata(VaultSecret secret, SecretPayload? payload)
    {
        if (string.IsNullOrEmpty(payload?.PrivateKeyContent))
        {
            return secret;
        }

        string? fingerprint = SshKeyFingerprint.Compute(payload.PrivateKeyContent, payload.Passphrase);
        string? keyType = SshKeyFingerprint.KeyTypeOf(payload.PrivateKeyContent, payload.Passphrase);
        return fingerprint == null && keyType == null
            ? secret
            : secret with
            {
                Fingerprint = secret.Fingerprint ?? fingerprint,
                KeyType = secret.KeyType ?? keyType,
            };
    }

    // 根据草稿新建材料条目（密码或私钥），完成校验与落库并返回元数据（保管库未解锁或取消返回 null）
    public async Task<VaultSecret?> CreateLibrarySecretFromDraftAsync(VaultSecretDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            return null;
        }

        if (draft.PrivateKey)
        {
            if (string.IsNullOrEmpty(draft.PrivateKeyContent))
            {
                return null;
            }

            var secret = new VaultSecret
            {
                Id = Guid.NewGuid(),
                Name = draft.Name.Trim(),
                Kind = SecretKind.SshPrivateKey,
            };
            bool saved = await SaveLibrarySecretAsync(secret, new SecretPayload
            {
                PrivateKeyContent = draft.PrivateKeyContent,
                Passphrase = draft.Passphrase,
            });
            return saved ? secret : null;
        }
        else
        {
            if (string.IsNullOrEmpty(draft.Password))
            {
                return null;
            }

            var secret = new VaultSecret
            {
                Id = Guid.NewGuid(),
                Name = draft.Name.Trim(),
                Kind = SecretKind.Password,
            };
            bool saved = await SaveLibrarySecretAsync(secret, new SecretPayload
            {
                Password = draft.Password,
            });
            return saved ? secret : null;
        }
    }
}
