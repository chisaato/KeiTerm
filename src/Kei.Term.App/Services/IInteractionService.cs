using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kei.Term.App.Models;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Security;
using Kei.Term.Core.Services;
using Kei.Term.Core.Vault;

namespace Kei.Term.App.Services;

// 所有「需要用户参与」的交互统一入口：弹窗、选择器、子窗口、通知。
// ViewModel 与服务只依赖本接口，由窗口层提供 Avalonia 实现；测试与无界面场景使用 NullInteractionService。
// 约定：返回 null / false 表示用户取消。
public interface IInteractionService
{
    // === 认证与 Vault ===
    Task<AuthPromptResult?> PromptAuthAsync(string prefillUsername, IReadOnlyList<VaultKeyOption> vaultKeys, AuthPromptMethod? defaultMethod);

    // keyboard-interactive 服务器提示（2FA 等）；取消 = null
    Task<string?> PromptKeyboardInteractiveAsync(string prompt);

    // 主密码懒解锁；error 为上次失败原因
    Task<string?> PromptMasterPasswordAsync(string? error);

    Task<PassphrasePromptResult?> PromptPassphraseAsync(FilePrivateKeyMethod method);

    // 关闭窗口 / 取消一律视为拒绝
    Task<HostKeyDecision> PromptHostKeyAsync(HostKeyEvaluation evaluation);

    // === 编辑器 ===
    Task<SessionNode?> EditSessionAsync(SessionNode? existing, Guid? parentId, IReadOnlyList<Identity> identities);

    Task<FolderNode?> EditFolderAsync(FolderNode? existing, Guid? parentId);

    Task<TerminalProfile?> EditTerminalProfileAsync(TerminalProfile? source, TerminalFontSnapshot font);

    // === 选择器 ===
    Task<string?> PickSecureCrtFolderAsync();

    Task<string?> PickKonsoleSchemeFileAsync();

    // === 子窗口 ===
    Task OpenIdentityManagerAsync();

    Task OpenKnownHostsAsync();

    Task OpenSettingsAsync();

    Task OpenQuickConnectAsync();

    // === 反馈 ===
    Task<bool> ConfirmDeleteAsync(string name);

    Task NotifyAsync(string title, string message);

    // 单行文本输入（重命名等）；取消 = null
    Task<string?> PromptTextAsync(string title, string label, string? initialText);
}

// 无界面实现：一切交互按「用户取消」处理；删除确认保持旧行为（无确认 UI 时直接放行）
public sealed class NullInteractionService : IInteractionService
{
    public static NullInteractionService Instance { get; } = new();

    public Task<AuthPromptResult?> PromptAuthAsync(string prefillUsername, IReadOnlyList<VaultKeyOption> vaultKeys, AuthPromptMethod? defaultMethod)
        => Task.FromResult<AuthPromptResult?>(null);

    public Task<string?> PromptKeyboardInteractiveAsync(string prompt) => Task.FromResult<string?>(null);

    public Task<string?> PromptMasterPasswordAsync(string? error) => Task.FromResult<string?>(null);

    public Task<PassphrasePromptResult?> PromptPassphraseAsync(FilePrivateKeyMethod method)
        => Task.FromResult<PassphrasePromptResult?>(null);

    public Task<HostKeyDecision> PromptHostKeyAsync(HostKeyEvaluation evaluation) => Task.FromResult(HostKeyDecision.Reject);

    public Task<SessionNode?> EditSessionAsync(SessionNode? existing, Guid? parentId, IReadOnlyList<Identity> identities)
        => Task.FromResult<SessionNode?>(null);

    public Task<FolderNode?> EditFolderAsync(FolderNode? existing, Guid? parentId) => Task.FromResult<FolderNode?>(null);

    public Task<TerminalProfile?> EditTerminalProfileAsync(TerminalProfile? source, TerminalFontSnapshot font)
        => Task.FromResult<TerminalProfile?>(null);

    public Task<string?> PickSecureCrtFolderAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickKonsoleSchemeFileAsync() => Task.FromResult<string?>(null);

    public Task OpenIdentityManagerAsync() => Task.CompletedTask;

    public Task OpenKnownHostsAsync() => Task.CompletedTask;

    public Task OpenSettingsAsync() => Task.CompletedTask;

    public Task OpenQuickConnectAsync() => Task.CompletedTask;

    public Task<bool> ConfirmDeleteAsync(string name) => Task.FromResult(true);

    public Task NotifyAsync(string title, string message) => Task.CompletedTask;

    public Task<string?> PromptTextAsync(string title, string label, string? initialText) => Task.FromResult<string?>(null);
}
