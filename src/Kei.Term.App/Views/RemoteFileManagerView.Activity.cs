using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class RemoteFileManagerView
{
    private RemoteFileManagerViewModel? _activityOwner;
    private ActivityTabPulse? _transferPulse;
    private ActivityTabPulse? _trackedPulse;
    private long _shownTransferRevision;
    private long _shownTrackedRevision;

    private void BindFileActivities()
    {
        if (!ReferenceEquals(_activityOwner, DataContext))
        {
            ReleaseFileActivities();
            if (DataContext is RemoteFileManagerViewModel model)
            {
                _activityOwner = model;
                model.PropertyChanged += OnFileActivityChanged;
            }
        }
        UpdateActivityPanelLayout();
        Dispatcher.UIThread.Post(AnimatePendingFileActivities, DispatcherPriority.Background);
    }

    private void OnFileActivityChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(RemoteFileManagerViewModel.SelectedActivityTab)) KeepActivityPanelHeightOnTabChange();
        if (args.PropertyName == nameof(RemoteFileManagerViewModel.IsActivityPanelOpen)) UpdateActivityPanelLayout();
        if (args.PropertyName is nameof(RemoteFileManagerViewModel.TransferActivityRevision)
            or nameof(RemoteFileManagerViewModel.TrackedActivityRevision)
            or nameof(RemoteFileManagerViewModel.IsActivityPanelOpen))
            Dispatcher.UIThread.Post(AnimatePendingFileActivities, DispatcherPriority.Background);
    }

    private void AnimatePendingFileActivities()
    {
        if (_activityOwner is not { IsActivityPanelOpen: true } model || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) == null) return;
        if (model.TransferActivityRevision > _shownTransferRevision)
        {
            _shownTransferRevision = model.TransferActivityRevision;
            (_transferPulse ??= new(TransferActivityHeader)).Start();
        }
        if (model.TrackedActivityRevision > _shownTrackedRevision)
        {
            _shownTrackedRevision = model.TrackedActivityRevision;
            (_trackedPulse ??= new(TrackedActivityHeader)).Start();
        }
    }

    private void ReleaseFileActivities()
    {
        if (_activityOwner != null) _activityOwner.PropertyChanged -= OnFileActivityChanged;
        _activityOwner = null;
        _transferPulse?.Cancel();
        _trackedPulse?.Cancel();
        _shownTransferRevision = _shownTrackedRevision = 0;
    }
}
