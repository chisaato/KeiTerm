using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kei.Term.App.ViewModels;

// 两类文件活动共享面板，数据更新只提醒对应页，不夺走用户正在查看的页。
public partial class RemoteFileManagerViewModel
{
    private readonly HashSet<FileTransferTaskItemViewModel> _observedTransfers = [];

    [ObservableProperty]
    private bool _isActivityPanelOpen;

    [ObservableProperty]
    private int _selectedActivityTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTransferActivity))]
    private long _transferActivityRevision;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrackedActivity))]
    private long _trackedActivityRevision;

    public bool HasTransferActivity => TransferActivityRevision > 0;
    public bool HasTrackedActivity => TrackedActivityRevision > 0;
    public int ActivityCount => TransferTasks.Count + ActiveTrackedFiles.Count;
    public bool HasFileActivities => ActivityCount > 0;

    private void InitializeFileActivities()
    {
        TransferTasks.CollectionChanged += OnTransferActivitiesChanged;
        ActiveTrackedFiles.CollectionChanged += OnTrackedActivitiesChanged;
    }

    private void OnTransferActivitiesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_disposed) return;
        foreach (FileTransferTaskItemViewModel task in _observedTransfers.Except(TransferTasks).ToArray())
        {
            task.PropertyChanged -= OnTransferActivityChanged;
            _observedTransfers.Remove(task);
        }
        foreach (FileTransferTaskItemViewModel task in TransferTasks)
            if (_observedTransfers.Add(task)) task.PropertyChanged += OnTransferActivityChanged;
        TransferActivityRevision++;
        UpdateFileActivities(args, 0);
    }

    private void OnTrackedActivitiesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_disposed) return;
        // 开始或停止监视只更新列表；实际文件回写才触发活动提醒。
        UpdateFileActivities(args, 1);
    }

    private void OnTransferActivityChanged(object? sender, PropertyChangedEventArgs args)
    {
        // 百分比与速度每一帧都可能变化；只在任务生命周期变化时发出提醒。
        if (!_disposed && args.PropertyName == nameof(FileTransferTaskItemViewModel.State)) TransferActivityRevision++;
    }

    private void UpdateFileActivities(NotifyCollectionChangedEventArgs args, int tab)
    {
        OnPropertyChanged(nameof(ActivityCount));
        OnPropertyChanged(nameof(HasFileActivities));
        if (!HasFileActivities) IsActivityPanelOpen = false;
        else if (!IsActivityPanelOpen && args.NewItems is { Count: > 0 }) ShowFileActivities(tab);
    }

    private void ShowFileActivities(int tab)
    {
        SelectedActivityTab = tab;
        IsActivityPanelOpen = true;
    }

    [RelayCommand]
    public void ToggleActivityPanel() => IsActivityPanelOpen = !IsActivityPanelOpen;

    private void DisposeFileActivities()
    {
        TransferTasks.CollectionChanged -= OnTransferActivitiesChanged;
        ActiveTrackedFiles.CollectionChanged -= OnTrackedActivitiesChanged;
        foreach (FileTransferTaskItemViewModel task in _observedTransfers) task.PropertyChanged -= OnTransferActivityChanged;
        _observedTransfers.Clear();
    }
}
