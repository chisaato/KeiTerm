namespace Kei.Term.App.ViewModels.Settings;

using System;
using CommunityToolkit.Mvvm.ComponentModel;

public partial class TransferOrderedItemViewModel<T> : ViewModelBase
    where T : struct, Enum
{
    public T Value { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isEnabled;

    public TransferOrderedItemViewModel(T value, string displayName, bool isEnabled)
    {
        Value = value;
        DisplayName = displayName;
        _isEnabled = isEnabled;
    }
}
