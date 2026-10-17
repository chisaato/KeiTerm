namespace Kei.Term.App.ViewModels.Settings;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;

public partial class TransferSettingsPage : ViewModelBase, ISettingsDrillDownHost
{
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

    [ObservableProperty]
    private int _concurrency = FileTransferSettings.DefaultConcurrency;

    [ObservableProperty]
    private string _defaultDownloadDirectory = string.Empty;

    [ObservableProperty]
    private ConflictPolicyOption _selectedConflictPolicy;

    public void SetConflictPolicy(TransferConflictPolicy policy)
        => SelectedConflictPolicy = ConflictPolicyOptions.FirstOrDefault(opt => opt.Policy == policy) ?? ConflictPolicyOptions[0];

    [ObservableProperty]
    private bool _resumeEnabled;

    [ObservableProperty]
    private bool _autoRetryEnabled = true;

    [ObservableProperty]
    private int _retryCount = FileTransferSettings.DefaultRetryCount;

    [ObservableProperty]
    private RetryBackoffOption _selectedBackoffMode;

    public void SetBackoffMode(RetryBackoffMode mode)
        => SelectedBackoffMode = BackoffModeOptions.FirstOrDefault(opt => opt.Mode == mode) ?? BackoffModeOptions[0];

    [ObservableProperty]
    private int _initialRetryIntervalSeconds = FileTransferSettings.DefaultInitialRetryIntervalSeconds;

    [ObservableProperty]
    private int _maxRetryIntervalSeconds = FileTransferSettings.DefaultMaxRetryIntervalSeconds;

    // 下钻状态管理
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInSubPage))]
    [NotifyPropertyChangedFor(nameof(SubPageTitle))]
    [NotifyPropertyChangedFor(nameof(SubPageContent))]
    private SettingsSubPageViewModel? _currentSubPage;

    public bool IsInSubPage => CurrentSubPage != null;

    // 视图绑定用的非空路径，避免未下钻时 CurrentSubPage 为 null 产生绑定警告
    public string SubPageTitle => CurrentSubPage?.Title ?? string.Empty;

    public object? SubPageContent => CurrentSubPage?.Content;

    public Services.IInteractionService Interaction { get; set; } = Services.NullInteractionService.Instance;

    public TransferSettingsPage()
    {
        _selectedConflictPolicy = ConflictPolicyOptions[0];
        _selectedBackoffMode = BackoffModeOptions[0];
    }

    public void NavigateToSubPage(string title, object content, Action? onClosed = null)
    {
        CurrentSubPage = new SettingsSubPageViewModel(title, content, onClosed);
    }

    [RelayCommand]
    public void NavigateBack()
    {
        if (CurrentSubPage != null)
        {
            Action? callback = CurrentSubPage.OnClosed;
            CurrentSubPage = null;
            callback?.Invoke();
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task BrowseDownloadDirectoryAsync()
    {
        string? folder = await Interaction.PickFolderAsync(Strings.Get("FileManager.DownloadToDialogTitle"));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            DefaultDownloadDirectory = folder;
        }
    }
}
