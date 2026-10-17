namespace Kei.Term.App.ViewModels.Settings;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;
using Kei.Term.App.Helpers;

public record FileSizeDisplayOption(FileSizeDisplayMode Mode, string Label);
public record ConflictPolicyOption(TransferConflictPolicy Policy, string Label);
public record RetryBackoffOption(RetryBackoffMode Mode, string Label);

public partial class FileTransferSettingsPage : ViewModelBase
{
    private readonly IExternalEditorRepository? _editorRepo;

    public IReadOnlyList<ConflictPolicyOption> ConflictPolicyOptions { get; } =
    [
        new(TransferConflictPolicy.Ask, Strings.Get("Settings.FileTransfer.ConflictPolicy.Ask")),
        new(TransferConflictPolicy.Overwrite, Strings.Get("Settings.FileTransfer.ConflictPolicy.Overwrite")),
        new(TransferConflictPolicy.Skip, Strings.Get("Settings.FileTransfer.ConflictPolicy.Skip")),
        new(TransferConflictPolicy.OverwriteIfOlder, Strings.Get("Settings.FileTransfer.ConflictPolicy.OverwriteIfOlder")),
        new(TransferConflictPolicy.AutoRename, Strings.Get("Settings.FileTransfer.ConflictPolicy.AutoRename")),
    ];

    public IReadOnlyList<RetryBackoffOption> BackoffModeOptions { get; } =
    [
        new(RetryBackoffMode.Exponential, Strings.Get("Settings.FileTransfer.BackoffMode.Exponential")),
        new(RetryBackoffMode.Fixed, Strings.Get("Settings.FileTransfer.BackoffMode.Fixed")),
    ];

    public IReadOnlyList<FileSizeDisplayOption> SizeDisplayOptions { get; } =
    [
        new(FileSizeDisplayMode.Iec, Strings.Get("Settings.FileTransfer.SizeIec")),
        new(FileSizeDisplayMode.Si, Strings.Get("Settings.FileTransfer.SizeSi")),
        new(FileSizeDisplayMode.Bytes, Strings.Get("Settings.FileTransfer.SizeBytes"))
    ];

    public FileSizeDisplayOption SelectedSizeDisplay
    {
        get => FileManagerPage.SelectedSizeDisplay;
        set => FileManagerPage.SelectedSizeDisplay = value;
    }

    public void SetSizeDisplayMode(FileSizeDisplayMode mode)
        => FileManagerPage.SetSizeDisplayMode(mode);

    // 会话覆盖的目录跟随默认值，复用设置窗口的同一份草稿。
    public TerminalSettingsPage Terminal { get; }

    // 转发属性到 _transferPage 与 _fileManagerPage 以保持测试与向后兼容性
    public TransferSettingsPage TransferPage { get; }
    public FileManagerSettingsPage FileManagerPage { get; }

    public int Concurrency
    {
        get => TransferPage.Concurrency;
        set => TransferPage.Concurrency = value;
    }

    public string DefaultDownloadDirectory
    {
        get => TransferPage.DefaultDownloadDirectory;
        set => TransferPage.DefaultDownloadDirectory = value;
    }

    public ConflictPolicyOption SelectedConflictPolicy
    {
        get => TransferPage.SelectedConflictPolicy;
        set => TransferPage.SelectedConflictPolicy = value;
    }

    public void SetConflictPolicy(TransferConflictPolicy policy)
        => TransferPage.SetConflictPolicy(policy);

    public bool ResumeEnabled
    {
        get => TransferPage.ResumeEnabled;
        set => TransferPage.ResumeEnabled = value;
    }

    public bool AutoRetryEnabled
    {
        get => TransferPage.AutoRetryEnabled;
        set => TransferPage.AutoRetryEnabled = value;
    }

    public int RetryCount
    {
        get => TransferPage.RetryCount;
        set => TransferPage.RetryCount = value;
    }

    public RetryBackoffOption SelectedBackoffMode
    {
        get => TransferPage.SelectedBackoffMode;
        set => TransferPage.SelectedBackoffMode = value;
    }

    public void SetBackoffMode(RetryBackoffMode mode)
        => TransferPage.SetBackoffMode(mode);

    public int InitialRetryIntervalSeconds
    {
        get => TransferPage.InitialRetryIntervalSeconds;
        set => TransferPage.InitialRetryIntervalSeconds = value;
    }

    public int MaxRetryIntervalSeconds
    {
        get => TransferPage.MaxRetryIntervalSeconds;
        set => TransferPage.MaxRetryIntervalSeconds = value;
    }

    public bool IsFileManagerOnLeft
    {
        get => FileManagerPage.IsFileManagerOnLeft;
        set => FileManagerPage.IsFileManagerOnLeft = value;
    }

    public int PollingIntervalSeconds
    {
        get => FileManagerPage.PollingIntervalSeconds;
        set => FileManagerPage.PollingIntervalSeconds = value;
    }

    public int WriteDebounceMilliseconds
    {
        get => FileManagerPage.WriteDebounceMilliseconds;
        set => FileManagerPage.WriteDebounceMilliseconds = value;
    }

    public string CustomEditorPath
    {
        get => FileManagerPage.CustomEditorPath;
        set => FileManagerPage.CustomEditorPath = value;
    }

    public string DisplayCustomEditorPath => FileManagerPage.DisplayCustomEditorPath;

    public string CacheDirectory
    {
        get => FileManagerPage.CacheDirectory;
        set => FileManagerPage.CacheDirectory = value;
    }

    public IReadOnlyList<string> WatcherModes => FileManagerPage.WatcherModes;

    public string SelectedWatcherMode
    {
        get => FileManagerPage.SelectedWatcherMode;
        set => FileManagerPage.SelectedWatcherMode = value;
    }

    public IReadOnlyList<TerminalUploadOption> TerminalUploadProtocols => FileManagerPage.TerminalUploadProtocols;
    public IReadOnlyList<TerminalDropActionOption> TerminalDropActions => FileManagerPage.TerminalDropActions;

    public TerminalUploadOption SelectedTerminalUploadProtocol
    {
        get => FileManagerPage.SelectedTerminalUploadProtocol;
        set => FileManagerPage.SelectedTerminalUploadProtocol = value;
    }

    public TerminalDropActionOption SelectedTerminalDropAction
    {
        get => FileManagerPage.SelectedTerminalDropAction;
        set => FileManagerPage.SelectedTerminalDropAction = value;
    }

    public void SetTerminalDropPreferences(TerminalUploadProtocol protocol, TerminalFileDropAction? action)
        => FileManagerPage.SetTerminalDropPreferences(protocol, action);

    public ObservableCollection<ExternalEditorItemViewModel> Editors => FileManagerPage.Editors;
    public ObservableCollection<FileAssociationItemViewModel> Associations => FileManagerPage.Associations;

    public ExternalEditorItemViewModel? SelectedEditor
    {
        get => FileManagerPage.SelectedEditor;
        set => FileManagerPage.SelectedEditor = value;
    }

    public FileAssociationItemViewModel? SelectedAssociation
    {
        get => FileManagerPage.SelectedAssociation;
        set => FileManagerPage.SelectedAssociation = value;
    }

    public Services.IInteractionService Interaction
    {
        get => FileManagerPage.Interaction;
        set
        {
            FileManagerPage.Interaction = value;
            TransferPage.Interaction = value;
        }
    }

    public string ErrorMessage
    {
        get => FileManagerPage.ErrorMessage;
        set => FileManagerPage.ErrorMessage = value;
    }

    public bool HasError => FileManagerPage.HasError;
    public bool HasEditors => FileManagerPage.HasEditors;
    public bool HasAssociations => FileManagerPage.HasAssociations;

    public IAsyncRelayCommand EditCustomEditorPathCommand => (IAsyncRelayCommand)FileManagerPage.EditCustomEditorPathCommand;
    public IAsyncRelayCommand AddEditorCommand => (IAsyncRelayCommand)FileManagerPage.AddEditorCommand;
    public IAsyncRelayCommand EditEditorCommand => (IAsyncRelayCommand)FileManagerPage.EditEditorCommand;
    public IAsyncRelayCommand DeleteEditorCommand => (IAsyncRelayCommand)FileManagerPage.DeleteEditorCommand;
    public IAsyncRelayCommand AddAssociationCommand => (IAsyncRelayCommand)FileManagerPage.AddAssociationCommand;
    public IAsyncRelayCommand EditAssociationCommand => (IAsyncRelayCommand)FileManagerPage.EditAssociationCommand;
    public IAsyncRelayCommand DeleteAssociationCommand => (IAsyncRelayCommand)FileManagerPage.DeleteAssociationCommand;
    public IRelayCommand ClearCacheDirectoryCommand => (IRelayCommand)FileManagerPage.ClearCacheDirectoryCommand;
    public IAsyncRelayCommand BrowseDownloadDirectoryCommand => (IAsyncRelayCommand)TransferPage.BrowseDownloadDirectoryCommand;

    public bool VerifyHashEnabled
    {
        get => TransferPage.VerifyHashEnabled;
        set => TransferPage.VerifyHashEnabled = value;
    }

    public bool ArchiveDirectoriesEnabled
    {
        get => TransferPage.ArchiveDirectoriesEnabled;
        set => TransferPage.ArchiveDirectoriesEnabled = value;
    }

    public ObservableCollection<TransferOrderedItemViewModel<TransferHashAlgorithm>> HashAlgorithms => TransferPage.HashAlgorithms;
    public ObservableCollection<TransferOrderedItemViewModel<TransferArchiveFormat>> ArchiveFormats => TransferPage.ArchiveFormats;

    public TransferOrderedItemViewModel<TransferHashAlgorithm>? SelectedHashAlgorithm
    {
        get => TransferPage.SelectedHashAlgorithm;
        set => TransferPage.SelectedHashAlgorithm = value;
    }

    public TransferOrderedItemViewModel<TransferArchiveFormat>? SelectedArchiveFormat
    {
        get => TransferPage.SelectedArchiveFormat;
        set => TransferPage.SelectedArchiveFormat = value;
    }

    public void InitializeTransferPreferences(
        bool verifyHashEnabled,
        IReadOnlyList<TransferHashAlgorithm>? hashOrder,
        bool archiveDirectoriesEnabled,
        IReadOnlyList<TransferArchiveFormat>? archiveOrder)
        => TransferPage.InitializeTransferPreferences(verifyHashEnabled, hashOrder, archiveDirectoriesEnabled, archiveOrder);

    public List<TransferHashAlgorithm> CollectHashAlgorithmOrder() => TransferPage.CollectHashAlgorithmOrder();
    public List<TransferArchiveFormat> CollectArchiveFormatOrder() => TransferPage.CollectArchiveFormatOrder();

    public IRelayCommand MoveUpHashAlgorithmCommand => (IRelayCommand)TransferPage.MoveUpHashAlgorithmCommand;
    public IRelayCommand MoveDownHashAlgorithmCommand => (IRelayCommand)TransferPage.MoveDownHashAlgorithmCommand;
    public IRelayCommand MoveUpArchiveFormatCommand => (IRelayCommand)TransferPage.MoveUpArchiveFormatCommand;
    public IRelayCommand MoveDownArchiveFormatCommand => (IRelayCommand)TransferPage.MoveDownArchiveFormatCommand;

    public FileTransferSettingsPage(IExternalEditorRepository? editorRepo = null, TerminalSettingsPage? terminal = null)
    {
        _editorRepo = editorRepo;
        Terminal = terminal ?? new TerminalSettingsPage();
        TransferPage = new TransferSettingsPage();
        FileManagerPage = new FileManagerSettingsPage(editorRepo, Terminal);

        TransferPage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) OnPropertyChanged(e.PropertyName);
        };
        FileManagerPage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) OnPropertyChanged(e.PropertyName);
        };
    }

    public async Task ReloadAsync()
    {
        await FileManagerPage.ReloadAsync();
        OnPropertyChanged(nameof(HasEditors));
        OnPropertyChanged(nameof(HasAssociations));
    }

}

public class ExternalEditorItemViewModel : ViewModelBase
{
    public ExternalEditor Model { get; }
    public Guid Id => Model.Id;
    public string Name => Model.Name;
    public bool IsDefault => Model.IsDefault;
    public string DisplayPath => Model.GetEffectivePath(PlatformHelper.CurrentOs) ?? "(当前平台未配置)";

    public ExternalEditorItemViewModel(ExternalEditor model)
    {
        Model = model;
    }
}

public class FileAssociationItemViewModel : ViewModelBase
{
    public FileAssociationRule Model { get; }
    public Guid Id => Model.Id;
    public string Pattern => Model.Pattern;
    public int Priority => Model.Priority;
    public string EditorName { get; }

    public FileAssociationItemViewModel(FileAssociationRule model, string editorName)
    {
        Model = model;
        EditorName = editorName;
    }
}
