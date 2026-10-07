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
using Kei.Term.App.Helpers;

public record FileSizeDisplayOption(FileSizeDisplayMode Mode, string Label);

public partial class FileTransferSettingsPage : ViewModelBase
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

    // 会话覆盖的目录跟随默认值，复用设置窗口的同一份草稿。
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

    // 文件传输缓存目录与监视模式
    [ObservableProperty]
    private string _cacheDirectory = string.Empty;

    public IReadOnlyList<string> WatcherModes { get; } = ["Auto", "OSNative", "Polling"];

    [ObservableProperty]
    private string _selectedWatcherMode = "Auto";

    // 外部编辑器列表与关联规则列表
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

    public FileTransferSettingsPage(IExternalEditorRepository? editorRepo = null, TerminalSettingsPage? terminal = null)
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
            Associations.Add(new FileAssociationItemViewModel(r, editor?.Name ?? "未知编辑器"));
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
    private Task EditCustomEditorPathAsync() => RunAsync(async () =>
    {
        string? path = await Interaction.PromptTextAsync(
            Strings.Get("Settings.FileTransfer.CustomEditorPath"),
            Strings.Get("Settings.FileTransfer.CustomEditorPathTip"), CustomEditorPath);
        // null 是取消，空字符串则明确恢复系统关联；这里只改草稿，统一应用后才落盘。
        if (path != null) CustomEditorPath = path.Trim();
    });

    [RelayCommand]
    private Task AddEditorAsync() => EditEditorCoreAsync(null);

    [RelayCommand(CanExecute = nameof(HasSelectedEditor))]
    private Task EditEditorAsync() => EditEditorCoreAsync(SelectedEditor?.Model);

    private Task EditEditorCoreAsync(ExternalEditor? existing) => RunAsync(async () =>
    {
        ExternalEditor? result = await Interaction.EditExternalEditorAsync(existing);
        if (result == null || _editorRepo == null) return;
        await _editorRepo.SaveEditorAsync(result);
        await ReloadAsync();
        SelectedEditor = Editors.FirstOrDefault(e => e.Id == result.Id);
    });

    [RelayCommand(CanExecute = nameof(HasSelectedEditor))]
    private Task DeleteEditorAsync() => RunAsync(async () =>
    {
        if (SelectedEditor == null || _editorRepo == null) return;
        await _editorRepo.DeleteEditorAsync(SelectedEditor.Id);
        await ReloadAsync();
    });

    [RelayCommand(CanExecute = nameof(CanAddAssociation))]
    private Task AddAssociationAsync() => EditAssociationCoreAsync(null);

    [RelayCommand(CanExecute = nameof(HasSelectedAssociation))]
    private Task EditAssociationAsync() => EditAssociationCoreAsync(SelectedAssociation?.Model);

    private Task EditAssociationCoreAsync(FileAssociationRule? existing) => RunAsync(async () =>
    {
        FileAssociationRule? result = await Interaction.EditFileAssociationAsync(existing, Editors.Select(e => e.Model).ToArray());
        if (result == null || _editorRepo == null) return;
        await _editorRepo.SaveAssociationAsync(result);
        await ReloadAsync();
        SelectedAssociation = Associations.FirstOrDefault(a => a.Id == result.Id);
    });

    [RelayCommand(CanExecute = nameof(HasSelectedAssociation))]
    private Task DeleteAssociationAsync() => RunAsync(async () =>
    {
        if (SelectedAssociation == null || _editorRepo == null) return;
        await _editorRepo.DeleteAssociationAsync(SelectedAssociation.Id);
        await ReloadAsync();
    });

    private async Task RunAsync(Func<Task> operation)
    {
        ErrorMessage = string.Empty;
        try { await operation(); }
        catch (Exception) { ErrorMessage = "操作失败，请检查本地配置数据库后重试。"; }
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
