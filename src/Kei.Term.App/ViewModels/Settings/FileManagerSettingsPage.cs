namespace Kei.Term.App.ViewModels.Settings;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;

public partial class FileManagerSettingsPage : ViewModelBase
{
    private readonly IExternalEditorRepository? _editorRepo;

    public IReadOnlyList<FileSizeDisplayOption> SizeDisplayOptions { get; } =
    [
        new(FileSizeDisplayMode.Iec, Strings.Get("Settings.FileTransfer.SizeIec")),
        new(FileSizeDisplayMode.Si, Strings.Get("Settings.FileTransfer.SizeSi")),
        new(FileSizeDisplayMode.Bytes, Strings.Get("Settings.FileTransfer.SizeBytes"))
    ];

    [ObservableProperty]
    private FileSizeDisplayOption _selectedSizeDisplay;

    public void SetSizeDisplayMode(FileSizeDisplayMode mode)
        => SelectedSizeDisplay = SizeDisplayOptions.FirstOrDefault(option => option.Mode == mode) ?? SizeDisplayOptions[0];

    public TerminalSettingsPage Terminal { get; }

    [ObservableProperty]
    private bool _isFileManagerOnLeft;

    [ObservableProperty]
    private int _pollingIntervalSeconds = 3;

    [ObservableProperty]
    private int _writeDebounceMilliseconds = 800;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayCustomEditorPath))]
    private string _customEditorPath = string.Empty;

    public string DisplayCustomEditorPath => string.IsNullOrWhiteSpace(CustomEditorPath)
        ? Strings.Get("Settings.FileTransfer.CustomEditorPathPlaceholder") : CustomEditorPath;

    [ObservableProperty]
    private string _cacheDirectory = string.Empty;

    public IReadOnlyList<string> WatcherModes { get; } = ["Auto", "OSNative", "Polling"];

    [ObservableProperty]
    private string _selectedWatcherMode = "Auto";

    public IReadOnlyList<TerminalUploadOption> TerminalUploadProtocols => TerminalFileDropOptions.UploadProtocols;
    public IReadOnlyList<TerminalDropActionOption> TerminalDropActions => TerminalFileDropOptions.DropActions;

    [ObservableProperty]
    private TerminalUploadOption _selectedTerminalUploadProtocol = TerminalFileDropOptions.UploadProtocols.Single(option => option.Protocol == TerminalUploadProtocol.Scp);

    [ObservableProperty]
    private TerminalDropActionOption _selectedTerminalDropAction = TerminalFileDropOptions.DropActions[0];

    public void SetTerminalDropPreferences(TerminalUploadProtocol protocol, TerminalFileDropAction? action)
    {
        SelectedTerminalUploadProtocol = TerminalUploadProtocols.FirstOrDefault(option => option.Protocol == protocol)
            ?? TerminalUploadProtocols.Single(option => option.Protocol == TerminalUploadProtocol.Scp);
        SelectedTerminalDropAction = TerminalDropActions.FirstOrDefault(option => option.Action == action) ?? TerminalDropActions[0];
    }

    public ObservableCollection<ExternalEditorItemViewModel> Editors { get; } = [];
    public ObservableCollection<FileAssociationItemViewModel> Associations { get; } = [];

    [ObservableProperty]
    private ExternalEditorItemViewModel? _selectedEditor;

    [ObservableProperty]
    private FileAssociationItemViewModel? _selectedAssociation;

    public Services.IInteractionService Interaction { get; set; } = Services.NullInteractionService.Instance;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasEditors => Editors.Count > 0;
    public bool HasAssociations => Associations.Count > 0;

    public FileManagerSettingsPage(IExternalEditorRepository? editorRepo = null, TerminalSettingsPage? terminal = null)
    {
        _editorRepo = editorRepo;
        Terminal = terminal ?? new TerminalSettingsPage();
        _selectedSizeDisplay = SizeDisplayOptions[0];
    }

    partial void OnSelectedEditorChanged(ExternalEditorItemViewModel? value)
    {
        EditEditorCommand.NotifyCanExecuteChanged();
        DeleteEditorCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedAssociationChanged(FileAssociationItemViewModel? value)
    {
        EditAssociationCommand.NotifyCanExecuteChanged();
        DeleteAssociationCommand.NotifyCanExecuteChanged();
    }

    public async Task ReloadAsync()
    {
        if (_editorRepo == null) return;

        var editors = await _editorRepo.GetAllEditorsAsync();
        var rules = await _editorRepo.GetAllAssociationsAsync();

        Guid? editorId = SelectedEditor?.Id;
        Guid? associationId = SelectedAssociation?.Id;
        Editors.Clear();
        foreach (var e in editors)
        {
            Editors.Add(new ExternalEditorItemViewModel(e));
        }

        Associations.Clear();
        foreach (var r in rules)
        {
            var editor = Editors.FirstOrDefault(e => e.Id == r.EditorId);
            Associations.Add(new FileAssociationItemViewModel(r, editor?.Name ?? Strings.Get("Settings.FileTransfer.UnknownEditor")));
        }
        SelectedEditor = Editors.FirstOrDefault(e => e.Id == editorId) ?? Editors.FirstOrDefault();
        SelectedAssociation = Associations.FirstOrDefault(a => a.Id == associationId) ?? Associations.FirstOrDefault();
        OnPropertyChanged(nameof(HasEditors));
        OnPropertyChanged(nameof(HasAssociations));
        AddAssociationCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearCacheDirectory()
    {
        if (string.IsNullOrWhiteSpace(CacheDirectory) || !Directory.Exists(CacheDirectory)) return;
        try
        {
            var dir = new DirectoryInfo(CacheDirectory);
            foreach (var file in dir.GetFiles()) file.Delete();
            foreach (var sub in dir.GetDirectories()) sub.Delete(true);
        }
        catch { }
    }

    private bool HasSelectedEditor() => SelectedEditor != null;
    private bool HasSelectedAssociation() => SelectedAssociation != null;
    private bool CanAddAssociation() => HasEditors;

    [RelayCommand]
    private async Task EditCustomEditorPathAsync() => await RunAsync(async () =>
    {
        string? path = await Interaction.PromptTextAsync(
            Strings.Get("Settings.FileTransfer.CustomEditorPath"),
            Strings.Get("Settings.FileTransfer.CustomEditorPathTip"), CustomEditorPath);
        if (path != null) CustomEditorPath = path.Trim();
    });

    [RelayCommand]
    private async Task AddEditorAsync() => await EditEditorCoreAsync(null);

    [RelayCommand(CanExecute = nameof(HasSelectedEditor))]
    private async Task EditEditorAsync() => await EditEditorCoreAsync(SelectedEditor?.Model);

    private async Task EditEditorCoreAsync(ExternalEditor? existing) => await RunAsync(async () =>
    {
        ExternalEditor? result = await Interaction.EditExternalEditorAsync(existing);
        if (result == null || _editorRepo == null) return;
        await _editorRepo.SaveEditorAsync(result);
        await ReloadAsync();
        SelectedEditor = Editors.FirstOrDefault(e => e.Id == result.Id);
    });

    [RelayCommand(CanExecute = nameof(HasSelectedEditor))]
    private async Task DeleteEditorAsync() => await RunAsync(async () =>
    {
        if (SelectedEditor == null || _editorRepo == null) return;
        await _editorRepo.DeleteEditorAsync(SelectedEditor.Id);
        await ReloadAsync();
    });

    [RelayCommand(CanExecute = nameof(CanAddAssociation))]
    private async Task AddAssociationAsync() => await EditAssociationCoreAsync(null);

    [RelayCommand(CanExecute = nameof(HasSelectedAssociation))]
    private async Task EditAssociationAsync() => await EditAssociationCoreAsync(SelectedAssociation?.Model);

    private async Task EditAssociationCoreAsync(FileAssociationRule? existing) => await RunAsync(async () =>
    {
        FileAssociationRule? result = await Interaction.EditFileAssociationAsync(existing, Editors.Select(e => e.Model).ToArray());
        if (result == null || _editorRepo == null) return;
        await _editorRepo.SaveAssociationAsync(result);
        await ReloadAsync();
        SelectedAssociation = Associations.FirstOrDefault(a => a.Id == result.Id);
    });

    [RelayCommand(CanExecute = nameof(HasSelectedAssociation))]
    private async Task DeleteAssociationAsync() => await RunAsync(async () =>
    {
        if (SelectedAssociation == null || _editorRepo == null) return;
        await _editorRepo.DeleteAssociationAsync(SelectedAssociation.Id);
        await ReloadAsync();
    });

    private async Task RunAsync(Func<Task> operation)
    {
        ErrorMessage = string.Empty;
        try { await operation(); }
        catch (Exception) { ErrorMessage = Strings.Get("Settings.FileTransfer.DatabaseOperationFailed"); }
    }
}
