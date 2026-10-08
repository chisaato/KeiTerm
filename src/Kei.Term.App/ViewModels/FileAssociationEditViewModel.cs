using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Models;

namespace Kei.Term.App.ViewModels;

public partial class FileAssociationEditViewModel : ViewModelBase
{
    private readonly FileAssociationRule _source;

    public FileAssociationEditViewModel(FileAssociationRule? existing, IReadOnlyList<ExternalEditor> editors)
    {
        _source = existing ?? new FileAssociationRule();
        Title = existing == null ? "新增文件类型关联" : "编辑文件类型关联";
        Editors = editors.Select(e => new ExternalEditorItemViewModel(e)).ToArray();
        _pattern = _source.Pattern;
        _priority = _source.Priority;
        _selectedEditor = Editors.FirstOrDefault(e => e.Id == _source.EditorId) ?? Editors.FirstOrDefault();
    }

    public string Title { get; }
    public IReadOnlyList<ExternalEditorItemViewModel> Editors { get; }
    public FileAssociationRule? Result { get; private set; }
    public bool IsConfirmed => Result != null;
    public event Action? RequestClose;

    [ObservableProperty] private string _pattern = string.Empty;
    [ObservableProperty] private ExternalEditorItemViewModel? _selectedEditor;
    [ObservableProperty] private int _priority;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _errorMessage = string.Empty;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Pattern) || SelectedEditor == null || !Editors.Any(e => e.Id == SelectedEditor.Id))
        {
            ErrorMessage = "请填写文件匹配模式并选择关联编辑器。";
            return;
        }
        Result = new FileAssociationRule
        {
            Id = _source.Id,
            Pattern = Pattern.Trim(),
            EditorId = SelectedEditor.Id,
            Priority = Priority,
            CreatedAt = _source.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        };
        ErrorMessage = string.Empty;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        RequestClose?.Invoke();
    }
}
