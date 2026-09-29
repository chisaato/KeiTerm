using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Kei.Term.App.Helpers;
using Kei.Term.App.Logging;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.BatchEdit;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Security;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Vault;
using Microsoft.Extensions.Logging;

namespace Kei.Term.App.Views;

// IInteractionService 的 Avalonia 实现：所有模态窗口以主窗口为 owner。
// 每个入口统一经 Safe.RunAsync 容错：异常记录后按"取消"语义返回，避免命令静默吞异常。
public sealed class MainWindowInteractionService : IInteractionService
{
    private readonly MainWindow _owner;
    private readonly MainViewModel _mainVm;
    private readonly IdentityManagerViewModel _identityManager;
    private readonly SettingsViewModel _settings;
    private readonly KnownHostsManagerViewModel? _knownHosts;
    private readonly ILogger _log;

    public MainWindowInteractionService(
        MainWindow owner,
        MainViewModel mainVm,
        IdentityManagerViewModel identityManager,
        SettingsViewModel settings,
        KnownHostsManagerViewModel? knownHosts,
        ILogger log)
    {
        _owner = owner;
        _mainVm = mainVm;
        _identityManager = identityManager;
        _settings = settings;
        _knownHosts = knownHosts;
        _log = log;
    }

    // === 认证与 Vault ===

    public Task<AuthPromptResult?> PromptAuthAsync(string prefillUsername, IReadOnlyList<VaultKeyOption> vaultKeys, AuthPromptMethod? defaultMethod)
        => Safe.RunAsync<AuthPromptResult?>(_log, "打开认证窗口",
            () => new AuthPromptWindow(prefillUsername, vaultKeys, defaultMethod).ShowDialog<AuthPromptResult?>(_owner));

    public Task<string?> PromptKeyboardInteractiveAsync(string prompt)
        => Safe.RunAsync<string?>(_log, "打开 KI 交互窗口",
            () => new AuthPromptWindow(prompt).ShowDialog<string?>(_owner));

    public Task<string?> PromptMasterPasswordAsync(string? error)
        => Safe.RunAsync<string?>(_log, "打开主密码窗口",
            () => new MasterPasswordWindow(error).ShowDialog<string?>(_owner));

    public Task<PassphrasePromptResult?> PromptPassphraseAsync(FilePrivateKeyMethod method)
        => Safe.RunAsync<PassphrasePromptResult?>(_log, "打开口令窗口",
            () => new PassphrasePromptWindow(method).ShowDialog<PassphrasePromptResult?>(_owner));

    // 关闭窗口时 ShowDialog 返回 default(HostKeyDecision) = Reject
    public Task<HostKeyDecision> PromptHostKeyAsync(HostKeyEvaluation evaluation)
        => Safe.RunAsync(_log, "打开主机密钥确认窗口",
            () => new HostKeyPromptWindow(evaluation).ShowDialog<HostKeyDecision>(_owner));

    // === 编辑器 ===

    public Task<SessionNode?> EditSessionAsync(SessionNode? existing, Guid? parentId, IReadOnlyList<Identity> identities)
        => Safe.RunAsync<SessionNode?>(_log, "打开会话编辑窗口", async () =>
        {
            _log.LogInformation("会话编辑窗口打开 模式={Mode}", existing == null ? "新建" : "编辑");
            var editVm = new SessionEditViewModel(existing, parentId, identities, _mainVm.CurrentSettings);
            await new SessionEditWindow(editVm).ShowDialog(_owner);
            SessionNode? result = editVm.IsConfirmed ? editVm.ApplyToModel(existing) : null;
            _log.LogInformation("会话编辑窗口关闭 结果={Result}", result == null ? "取消" : "确认");
            return result;
        });

    public Task<FolderNode?> EditFolderAsync(FolderNode? existing, Guid? parentId)
        => Safe.RunAsync<FolderNode?>(_log, "打开文件夹编辑窗口", async () =>
        {
            _log.LogInformation("文件夹编辑窗口打开 模式={Mode}", existing == null ? "新建" : "编辑");
            var editVm = new FolderEditViewModel(existing, parentId);
            await new FolderEditWindow(editVm).ShowDialog(_owner);
            FolderNode? result = editVm.IsConfirmed ? editVm.ApplyToModel(existing) : null;
            _log.LogInformation("文件夹编辑窗口关闭 结果={Result}", result == null ? "取消" : "确认");
            return result;
        });

    public Task<TerminalProfile?> EditTerminalProfileAsync(TerminalProfile? source, TerminalFontSnapshot font)
        => Safe.RunAsync<TerminalProfile?>(_log, "打开配色编辑窗口", async () =>
        {
            var editVm = new TerminalProfileEditViewModel(source, font);
            bool confirmed = await new TerminalProfileEditWindow(editVm).ShowDialog<bool>(_owner);
            return confirmed && editVm.IsConfirmed ? editVm.ResultProfile : null;
        });

    // === 选择器 ===

    public Task<string?> PickSecureCrtFolderAsync()
        => Safe.RunAsync<string?>(_log, "选择 SecureCRT 目录", async () =>
        {
            IReadOnlyList<IStorageFolder> folders = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择 SecureCRT Sessions 目录",
                AllowMultiple = false
            });
            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        });

    public Task<string?> PickKonsoleSchemeFileAsync()
        => Safe.RunAsync<string?>(_log, "选择 Konsole 配色文件", async () =>
        {
            IReadOnlyList<IStorageFile> files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Get("Menu.File.ImportKonsole"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Konsole Color Scheme (*.colorscheme)") { Patterns = ["*.colorscheme"] },
                    new FilePickerFileType("All Files (*.*)") { Patterns = ["*.*"] }
                ]
            });
            return files.Count > 0 ? files[0].Path.LocalPath : null;
        });

    // === 子窗口 ===

    public Task OpenIdentityManagerAsync() => Safe.RunAsync(_log, "打开身份管理器", async () =>
    {
        _log.LogInformation("身份管理器打开");
        await _identityManager.LoadAsync();
        await new IdentityManagerWindow(_identityManager, _mainVm.Logger).ShowDialog(_owner);
        _log.LogInformation("身份管理器关闭");
    });

    public Task OpenKnownHostsAsync() => Safe.RunAsync(_log, "打开已知主机", async () =>
    {
        if (_knownHosts == null)
        {
            return;
        }

        await _knownHosts.LoadAsync();
        await new KnownHostsManagerWindow(_knownHosts).ShowDialog(_owner);
    });

    public Task OpenSettingsAsync() => Safe.RunAsync(_log, "打开设置", async () =>
    {
        _log.LogInformation("设置窗口打开");
        await new SettingsWindow(_settings).ShowDialog(_owner);
        _log.LogInformation("设置窗口关闭");

        AppSettings current = _mainVm.CurrentSettings;
        if (_settings.IsConfirmed)
        {
            // 设置确认保存后立即应用控件库主题（Apply 幂等，KeiClassic 即卸载第三方主题）与树密度
            UiDesignSystemService.Apply(current.ControlLibraryTheme);
            UiDesignSystemService.ApplyTreeDensity(
                current.TreeItemHeight,
                current.TreeFontSize,
                current.TreeIconSize,
                current.TreeIndent);
            // 排序模式可能变更，立即刷新树排序
            _ = _mainVm.ReloadTreeAsync();
        }

        // 确认则应用新位置，取消则恢复原位置（两者都以已提交设置为准）
        if (Enum.TryParse(current.TabPlacement, true, out TabPlacement placement))
        {
            _mainVm.TabPlacement = placement;
            _owner.UpdateTabPlacement(placement);
        }
    });

    public Task OpenQuickConnectAsync() => Safe.RunAsync(_log, "打开快速连接窗口", async () =>
    {
        _log.LogInformation("快速连接窗口打开");
        var quickVm = new QuickConnectViewModel(_mainVm.CurrentSettings);
        await new QuickConnectWindow(quickVm).ShowDialog(_owner);
        if (!quickVm.IsConfirmed)
        {
            _log.LogInformation("快速连接窗口取消");
            return;
        }

        // 密码仅本次内存流转；勾选保存时仅落库元数据
        await _mainVm.ConnectQuickAsync(quickVm.Host, quickVm.Port, quickVm.Username, quickVm.Password, quickVm.SaveAsSession);
    });

    // === 反馈 ===

    // 独立确认框尚未实现：保持现有行为直接放行
    public Task<bool> ConfirmDeleteAsync(string name) => Task.FromResult(true);

    public Task OpenBatchEditAsync(BatchSessionEditViewModel viewModel) => Safe.RunAsync(_log, "打开批量修改会话", async () =>
    {
        _log.LogInformation("批量修改会话窗口打开");
        await new BatchSessionEditWindow(viewModel).ShowDialog(_owner);
        _log.LogInformation("批量修改会话窗口关闭 已写入={Applied} 变更会话数={Count}", viewModel.ApplyAttempted, viewModel.ChangedCount);
    });

    public Task<string?> PromptTextAsync(string title, string label, string? initialText)
        => Safe.RunAsync<string?>(_log, "打开文本输入窗口",
            () => new TextPromptWindow(title, label, initialText).ShowDialog<string?>(_owner));

    // 统一消息通知：更新主窗口状态提示栏，并在日志留痕
    public Task NotifyAsync(string title, string message)
    {
        _log.LogInformation("[通知] {Title}: {Message}", title, message);
        _mainVm.StatusMessage = $"{title}: {message.Replace("\n", " ")}";
        return Task.CompletedTask;
    }
}
