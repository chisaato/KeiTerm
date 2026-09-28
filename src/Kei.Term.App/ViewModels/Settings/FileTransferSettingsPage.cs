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

public partial class FileTransferSettingsPage : ViewModelBase
{
    private readonly IExternalEditorRepository? _editorRepo;

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

    // 交互编辑区：外部编辑器
    [ObservableProperty]
    private string _editingEditorName = string.Empty;

    [ObservableProperty]
    private string _editingEditorOs = PlatformHelper.CurrentOs;

    [ObservableProperty]
    private string _editingEditorPath = string.Empty;

    [ObservableProperty]
    private string _editingEditorArgs = "\"{path}\"";

    [ObservableProperty]
    private bool _editingEditorIsDefault;

    // 交互编辑区：文件关联
    [ObservableProperty]
    private string _editingAssocPattern = string.Empty;

    [ObservableProperty]
    private ExternalEditorItemViewModel? _editingAssocSelectedEditor;

    [ObservableProperty]
    private int _editingAssocPriority;

    public IReadOnlyList<string> SupportedOsList { get; } = ["any", "windows", "linux", "macos", "android"];

    public FileTransferSettingsPage(IExternalEditorRepository? editorRepo = null)
    {
        _editorRepo = editorRepo;
    }

    partial void OnSelectedEditorChanged(ExternalEditorItemViewModel? value)
    {
        if (value == null) return;
        EditingEditorName = value.Name;
        EditingEditorIsDefault = value.IsDefault;
        EditingEditorArgs = value.Model.ArgumentsTemplate;

        // 如果包含当前 OS 路径，填入
        var currentOs = PlatformHelper.CurrentOs;
        var p = value.Model.Paths.FirstOrDefault(x => string.Equals(x.Os, currentOs, StringComparison.OrdinalIgnoreCase))
             ?? value.Model.Paths.FirstOrDefault(x => string.Equals(x.Os, "any", StringComparison.OrdinalIgnoreCase))
             ?? value.Model.Paths.FirstOrDefault();

        if (p != null)
        {
            EditingEditorOs = p.Os;
            EditingEditorPath = p.Path;
        }
    }

    partial void OnSelectedAssociationChanged(FileAssociationItemViewModel? value)
    {
        if (value == null) return;
        EditingAssocPattern = value.Pattern;
        EditingAssocPriority = value.Priority;
        EditingAssocSelectedEditor = Editors.FirstOrDefault(e => e.Id == value.Model.EditorId);
    }

    public async Task ReloadAsync()
    {
        if (_editorRepo == null) return;

        var editors = await _editorRepo.GetAllEditorsAsync();
        var rules = await _editorRepo.GetAllAssociationsAsync();

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

    [RelayCommand]
    private async Task AddOrUpdateEditorAsync()
    {
        if (string.IsNullOrWhiteSpace(EditingEditorName) || string.IsNullOrWhiteSpace(EditingEditorPath) || _editorRepo == null)
        {
            return;
        }

        var editor = SelectedEditor?.Model ?? new ExternalEditor { Name = EditingEditorName };
        editor.Name = EditingEditorName.Trim();
        editor.ArgumentsTemplate = string.IsNullOrWhiteSpace(EditingEditorArgs) ? "\"{path}\"" : EditingEditorArgs.Trim();
        editor.IsDefault = EditingEditorIsDefault;

        // 更新或添加当前 OS 路径
        var existingPath = editor.Paths.FirstOrDefault(p => string.Equals(p.Os, EditingEditorOs, StringComparison.OrdinalIgnoreCase));
        if (existingPath != null)
        {
            editor.Paths.Remove(existingPath);
        }
        editor.Paths.Add(new ExternalEditorPath(Guid.NewGuid(), editor.Id, EditingEditorOs, EditingEditorPath.Trim()));

        await _editorRepo.SaveEditorAsync(editor);
        await ReloadAsync();

        // 重置编辑区
        EditingEditorName = string.Empty;
        EditingEditorPath = string.Empty;
        SelectedEditor = null;
    }

    [RelayCommand]
    private async Task DeleteSelectedEditorAsync()
    {
        if (SelectedEditor == null || _editorRepo == null) return;
        await _editorRepo.DeleteEditorAsync(SelectedEditor.Id);
        await ReloadAsync();
        SelectedEditor = null;
    }

    [RelayCommand]
    private async Task AddOrUpdateAssociationAsync()
    {
        if (string.IsNullOrWhiteSpace(EditingAssocPattern) || EditingAssocSelectedEditor == null || _editorRepo == null)
        {
            return;
        }

        var rule = SelectedAssociation?.Model ?? new FileAssociationRule();
        rule.Pattern = EditingAssocPattern.Trim();
        rule.EditorId = EditingAssocSelectedEditor.Id;
        rule.Priority = EditingAssocPriority;

        await _editorRepo.SaveAssociationAsync(rule);
        await ReloadAsync();

        EditingAssocPattern = string.Empty;
        SelectedAssociation = null;
    }

    [RelayCommand]
    private async Task DeleteSelectedAssociationAsync()
    {
        if (SelectedAssociation == null || _editorRepo == null) return;
        await _editorRepo.DeleteAssociationAsync(SelectedAssociation.Id);
        await ReloadAsync();
        SelectedAssociation = null;
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
