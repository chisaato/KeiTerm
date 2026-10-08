using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Security;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Vault;

namespace Kei.Term.Tests;

// 按脚本回答的交互服务：每类提示一个队列，队列耗尽即按"取消"处理；同时记录调用次数与参数
internal sealed class ScriptedInteraction : IInteractionService
{
    public Queue<ExternalEditor?> EditorResults { get; } = new();
    public Queue<FileAssociationRule?> AssociationResults { get; } = new();
    public Task<ExternalEditor?> EditExternalEditorAsync(ExternalEditor? existing)
        => Task.FromResult(EditorResults.Count > 0 ? EditorResults.Dequeue() : null);
    public Task<FileAssociationRule?> EditFileAssociationAsync(FileAssociationRule? existing, IReadOnlyList<ExternalEditor> editors)
        => Task.FromResult(AssociationResults.Count > 0 ? AssociationResults.Dequeue() : null);

    public Queue<string?> MasterPasswords { get; } = new();
    public Queue<PassphrasePromptResult?> Passphrases { get; } = new();
    public Queue<AuthPromptResult?> AuthResults { get; } = new();
    public Queue<string?> KeyboardInteractiveAnswers { get; } = new();
    public Queue<HostKeyDecision> HostKeyDecisions { get; } = new();

    public int MasterPasswordPrompts { get; private set; }
    public int PassphrasePrompts { get; private set; }
    public List<string> AuthPromptUsernames { get; } = [];
    public List<string> KeyboardInteractivePrompts { get; } = [];
    public List<HostKeyEvaluation> HostKeyPrompts { get; } = [];
    public List<(string Title, string Message)> Notifications { get; } = [];

    public Task<string?> PromptMasterPasswordAsync(string? error)
    {
        MasterPasswordPrompts++;
        return Task.FromResult(MasterPasswords.Count > 0 ? MasterPasswords.Dequeue() : null);
    }

    public Task<PassphrasePromptResult?> PromptPassphraseAsync(FilePrivateKeyMethod method)
    {
        PassphrasePrompts++;
        return Task.FromResult(Passphrases.Count > 0 ? Passphrases.Dequeue() : null);
    }

    public Task<AuthPromptResult?> PromptAuthAsync(string prefillUsername, IReadOnlyList<VaultKeyOption> vaultKeys, AuthPromptMethod? defaultMethod)
    {
        AuthPromptUsernames.Add(prefillUsername);
        return Task.FromResult(AuthResults.Count > 0 ? AuthResults.Dequeue() : null);
    }

    public Task<string?> PromptKeyboardInteractiveAsync(string prompt)
    {
        KeyboardInteractivePrompts.Add(prompt);
        return Task.FromResult(KeyboardInteractiveAnswers.Count > 0 ? KeyboardInteractiveAnswers.Dequeue() : null);
    }

    public Task<HostKeyDecision> PromptHostKeyAsync(HostKeyEvaluation evaluation)
    {
        HostKeyPrompts.Add(evaluation);
        return Task.FromResult(HostKeyDecisions.Count > 0 ? HostKeyDecisions.Dequeue() : HostKeyDecision.Reject);
    }

    public Task NotifyAsync(string title, string message)
    {
        Notifications.Add((title, message));
        return Task.CompletedTask;
    }

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

    public Func<Task>? QuickConnectAction { get; set; }
    public Func<Kei.Term.App.ViewModels.CommandPaletteViewModel, Kei.Term.App.ViewModels.CommandPaletteItem?>? PaletteSelection { get; set; }
    public Task<Kei.Term.App.ViewModels.CommandPaletteItem?> ShowCommandPaletteAsync(Kei.Term.App.ViewModels.CommandPaletteViewModel model)
        => Task.FromResult(PaletteSelection?.Invoke(model));
    public Task OpenQuickConnectAsync() => QuickConnectAction?.Invoke() ?? Task.CompletedTask;
    public Action? CloseWindowAction { get; set; }
    public Task CloseWindowAsync()
    {
        CloseWindowAction?.Invoke();
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmDeleteAsync(string name) => Task.FromResult(true);

    public Queue<string?> TextAnswers { get; } = new();

    public Task OpenBatchEditAsync(Kei.Term.App.ViewModels.BatchEdit.BatchSessionEditViewModel viewModel) => Task.CompletedTask;

    public Task<string?> PromptTextAsync(string title, string label, string? initialText)
        => Task.FromResult(TextAnswers.Count > 0 ? TextAnswers.Dequeue() : null);
}

// 纯内存设置服务
internal sealed class FixedSettingsService : ISettingsService
{
    public FixedSettingsService(AppSettings? settings = null) => Current = settings ?? new AppSettings();

    public AppSettings Current { get; private set; }

    public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(Current);

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        Current = settings;
        return Task.CompletedTask;
    }
}
