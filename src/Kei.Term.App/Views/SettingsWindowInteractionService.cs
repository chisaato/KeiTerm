using System;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kei.Term.App.Models;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.BatchEdit;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Security;
using Kei.Term.Core.Services;
using Kei.Term.Core.Transfers;
using Kei.Term.Core.Vault;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Kei.Term.App.Helpers;
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

    // === 同步交互统一入口 ===
    public Task<Kei.Term.Core.Sync.SyncJoinChoice?> PromptSyncJoinAsync(Kei.Term.Core.Sync.SyncConfirmation preview)
        => new Sync.SyncJoinChoiceWindow(preview).ShowDialog<Kei.Term.Core.Sync.SyncJoinChoice?>(DialogOwner);

    public Task<bool> ConfirmSyncDeletionAsync(Kei.Term.Core.Sync.SyncConfirmation preview)
        => new Sync.SyncDeletionConfirmationWindow(preview).ShowDialog<bool>(DialogOwner);

    public Task<string?> PromptSyncPassphraseAsync(bool changing, string? error = null)
        => new Sync.SyncPassphrasePromptWindow(changing, error).ShowDialog<string?>(DialogOwner);

    public Task<bool?> PromptDisableSyncAsync()
        => new Sync.SyncDisablePromptWindow().ShowDialog<bool?>(DialogOwner);

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

    public Task<string?> PromptMasterPasswordAsync(string? error)
        => MasterPasswordWindow.CreatePasswordConfirmationDialog(error).ShowDialog<string?>(DialogOwner);

    public Task<PassphrasePromptResult?> PromptPassphraseAsync(FilePrivateKeyMethod method)
        => Task.FromResult<PassphrasePromptResult?>(null);

    public Task<HostKeyDecision> PromptHostKeyAsync(HostKeyEvaluation evaluation) => Task.FromResult(HostKeyDecision.Reject);

    public Task<SessionNode?> EditSessionAsync(SessionNode? existing, Guid? parentId, IReadOnlyList<Identity> identities)
        => Task.FromResult<SessionNode?>(null);

    public Task<FolderNode?> EditFolderAsync(FolderNode? existing, Guid? parentId) => Task.FromResult<FolderNode?>(null);

    public Task<TerminalProfile?> EditTerminalProfileAsync(TerminalProfile? source, TerminalFontSnapshot font)
        => TerminalProfileEditorDialogs.EditAsync(owner, this, source, font);

    public Task<TerminalProfile?> ImportTerminalThemeJsonAsync(TerminalFontSnapshot font)
        => TerminalProfileEditorDialogs.EditAsync(owner, this, null, font, importJson: true);

    public Task<TerminalProfile?> EditTerminalThemeJsonAsync(TerminalProfile? source)
        => TerminalProfileEditorDialogs.EditJsonAsync(owner, source);

    public Task<string?> PickSecureCrtFolderAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickKonsoleSchemeFileAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickKonsoleProfileFileAsync() => KonsoleProfileFilePicker.PickAsync(DialogOwner);

    public Task OpenIdentityManagerAsync() => Task.CompletedTask;

    public Task OpenKnownHostsAsync() => Task.CompletedTask;

    public Task OpenSettingsAsync() => Task.CompletedTask;

    public Task OpenQuickConnectAsync() => Task.CompletedTask;

    public Task<bool> ConfirmDeleteAsync(string name)
        => ShowConfirmationAsync(DialogOwner, Strings.Get("Settings.Security.RemoveMasterPassword"), name);

    internal static Task<bool> ShowConfirmationAsync(Window owner, string title, string name)
    {
        var message = new TextBlock
        {
            Text = name,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            FontSize = 13,
        };
        message.Bind(TextBlock.ForegroundProperty, message.GetResourceObservable("Kei.Text.Primary"));
        var cancel = new Button { Content = Strings.Get("Common.Cancel"), MinWidth = 76, IsCancel = true };
        var confirm = new Button { Content = Strings.Get("Common.Confirm"), MinWidth = 76, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        confirm.Classes.Add("accent");
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20, 18, 20, 16),
                Spacing = 14,
                Children =
                {
                    message,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, confirm },
                    },
                },
            },
        };
        dialog.Bind(Window.BackgroundProperty, dialog.GetResourceObservable("Kei.Bg.Window"));
        dialog.Bind(Window.FontFamilyProperty, dialog.GetResourceObservable("Kei.Font.UI"));
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        return dialog.ShowDialog<bool>(owner);
    }

    public Task NotifyAsync(string title, string message, Kei.Term.App.Models.NotificationLevel level = Kei.Term.App.Models.NotificationLevel.Info) => Task.CompletedTask;

    public Task<string?> PromptTextAsync(string title, string label, string? initialText)
        => new TextPromptWindow(title, label, initialText).ShowDialog<string?>(DialogOwner);

    public Task<TransferConflictChoice?> PromptTransferConflictAsync(
        string fileName, TransferFileFacts source, TransferFileFacts target, CancellationToken ct = default)
    {
        TransferConflictWindow dialog = new(fileName, source, target);
        ct.Register(() => dialog.Close(null));
        return dialog.ShowDialog<TransferConflictChoice?>(DialogOwner);
    }

    public Task<bool> ConfirmLargeScpTransferAsync(string fileName, long size, CancellationToken ct = default)
        => TransferConflictWindow.ConfirmLargeScpAsync(DialogOwner, fileName, size);

    public async Task<string?> PickFolderAsync(string title, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<IStorageFolder> folders = await DialogOwner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public Task OpenBatchEditAsync(BatchSessionEditViewModel viewModel) => Task.CompletedTask;

    public async Task<IReadOnlyList<string>?> PickFilesAsync(string title, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<IStorageFile> files = await DialogOwner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
        });
        ct.ThrowIfCancellationRequested();
        return files.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
    }
}
