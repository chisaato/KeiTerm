using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kei.Term.App.Models;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.BatchEdit;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Security;
using Kei.Term.Core.Services;
using Kei.Term.Core.Vault;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Kei.Term.App.Services;

namespace Kei.Term.App.Views;

internal sealed class SettingsWindowInteractionService(Window owner, SettingsViewModel settings) : IInteractionService
{
    private Window DialogOwner
    {
        get
        {
            Window current = owner;
            while (current.OwnedWindows.LastOrDefault(w => w.IsVisible) is { } child) current = child;
            return current;
        }
    }

    public async Task<ExternalEditor?> EditExternalEditorAsync(ExternalEditor? existing)
    {
        ExternalEditorEditViewModel draft = new(existing, this);
        await new ExternalEditorEditWindow(draft).ShowDialog(DialogOwner);
        return draft.Result;
    }

    public async Task<FileAssociationRule?> EditFileAssociationAsync(FileAssociationRule? existing, IReadOnlyList<ExternalEditor> editors)
    {
        FileAssociationEditViewModel draft = new(existing, editors);
        await new FileAssociationEditWindow(draft).ShowDialog(DialogOwner);
        return draft.Result;
    }

    public async Task<string?> PickEditorExecutableAsync()
    {
        IStorageProvider storage = DialogOwner.StorageProvider;
        if (OperatingSystem.IsMacOS())
        {
            // Avalonia 文件选择器会过滤作为目录返回的 .app，目录选择器保留应用包。
            IReadOnlyList<IStorageFolder> applications = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择编辑器应用（.app）",
                AllowMultiple = false,
                SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(new Uri("file:///Applications/"))
            });
            return applications.FirstOrDefault()?.TryGetLocalPath();
        }
        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择外部编辑器",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<ProxyEditCommit?> EditProxyAsync(ProxyProfile? existing, bool hasSavedPassword)
    {
        ProxyEditViewModel draft = new(existing, hasSavedPassword: hasSavedPassword);
        if (existing?.Config is SessionProxyConfig session)
        {
            SessionNode? node = FlattenSessions(settings.SessionTreeSnapshot?.Invoke() ?? []).FirstOrDefault(n => n.Id == session.SessionId);
            draft.SessionDisplay = node == null ? "已删除的会话" : ViewModels.Settings.ProxySettingsPage.FormatSession(node);
        }
        draft.PickSessionAsync = PickSessionForProxyAsync;
        await new ProxyEditWindow(draft).ShowDialog(DialogOwner);
        if (!draft.IsConfirmed || draft.Build() is not { } profile) return null;
        return new ProxyEditCommit { Profile = profile, ApplyPassword = store => draft.ApplyPasswordAsync(store) };
    }

    private async Task<SessionNode?> PickSessionForProxyAsync()
    {
        IReadOnlyList<TreeNodeBase> roots = settings.SessionTreeSnapshot?.Invoke() ?? [];
        SessionPickerViewModel picker = new(roots, Guid.Empty, FlattenSessions(roots));
        return await new SessionPickerWindow(picker).ShowDialog<SessionNode?>(DialogOwner);
    }

    private static List<SessionNode> FlattenSessions(IEnumerable<TreeNodeBase> nodes)
    {
        List<SessionNode> sessions = [];
        foreach (TreeNodeBase node in nodes)
        {
            if (node is SessionNode session) sessions.Add(session);
            if (node is FolderNode folder) sessions.AddRange(FlattenSessions(folder.Children));
            else if (node is VirtualRootNode root) sessions.AddRange(FlattenSessions(root.Children));
        }
        return sessions;
    }

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

    public Task OpenBatchEditAsync(BatchSessionEditViewModel viewModel) => Task.CompletedTask;
}
