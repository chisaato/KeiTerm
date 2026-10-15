using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Services;

namespace Kei.Term.App.ViewModels;

public partial class RemoteFileManagerViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasTerminalDirectory)), NotifyCanExecuteChangedFor(nameof(JumpToTerminalDirectoryCommand))]
    private string? _terminalDirectory;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(TerminalDirectorySyncHint))]
    private string _terminalDirectoryStatus = string.Empty;

    public string TerminalDirectorySyncHint => string.IsNullOrEmpty(TerminalDirectoryStatus)
        ? Strings.Get("FileManager.CwdFollow.SyncTip") : TerminalDirectoryStatus;

    public bool HasTerminalDirectory => TerminalDirectoryTracker.IsValidDirectory(TerminalDirectory) && !_disposed;

    [RelayCommand(CanExecute = nameof(HasTerminalDirectory))]
    private Task JumpToTerminalDirectoryAsync()
        => TerminalDirectory is { } directory ? NavigateToTerminalDirectoryAsync(directory) : Task.CompletedTask;

    public async Task<bool> NavigateToTerminalDirectoryAsync(string directory, CancellationToken ct = default)
    {
        if (_disposed || !TerminalDirectoryTracker.IsValidDirectory(directory)) return false;
        ct.ThrowIfCancellationRequested();
        if (CurrentPath == directory && _loadedDirectoryPath == directory) return true;
        CurrentPath = directory;
        await RefreshDirectoryAsync(ct);
        return !ct.IsCancellationRequested && CurrentPath == directory && _loadedDirectoryPath == directory;
    }
}
