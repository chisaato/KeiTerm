namespace Kei.Term.App.ViewModels.Settings;

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

public partial class SettingsSubPageViewModel : ViewModelBase
{
    public string Title { get; }
    public object Content { get; }
    public Action? OnClosed { get; }

    public SettingsSubPageViewModel(string title, object content, Action? onClosed = null)
    {
        Title = title;
        Content = content;
        OnClosed = onClosed;
    }
}
