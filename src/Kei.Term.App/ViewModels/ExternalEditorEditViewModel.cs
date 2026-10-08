using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Services;
using Kei.Term.Core.Models;

namespace Kei.Term.App.ViewModels;

public partial class ExternalEditorEditViewModel : ViewModelBase
{
    private readonly ExternalEditor _source;
    private readonly IInteractionService _interaction;
    private readonly Dictionary<string, string> _paths;

    public ExternalEditorEditViewModel(ExternalEditor? existing = null, IInteractionService? interaction = null)
    {
        _source = existing ?? new ExternalEditor();
        _interaction = interaction ?? NullInteractionService.Instance;
        _paths = _source.Paths.ToDictionary(p => p.Os, p => p.Path, StringComparer.OrdinalIgnoreCase);
        Title = existing == null ? "新增外部编辑器" : "编辑外部编辑器";
        _name = _source.Name;
        _isDefault = _source.IsDefault;
        _path = _paths.GetValueOrDefault(SelectedOs) ?? _paths.GetValueOrDefault("any") ?? string.Empty;
    }

    public string Title { get; }
    public IReadOnlyList<string> SupportedOsList { get; } = ["macos", "windows", "linux", "any", "android"];
    public bool CanBrowse => string.Equals(SelectedOs, PlatformHelper.CurrentOs, StringComparison.OrdinalIgnoreCase) || SelectedOs == "any";
    public ExternalEditor? Result { get; private set; }
    public bool IsConfirmed => Result != null;
    public event Action? RequestClose;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _selectedOs = PlatformHelper.CurrentOs;
    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private bool _isDefault;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _errorMessage = string.Empty;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnSelectedOsChanging(string value)
    {
        // 切换平台前保留草稿；取消对话框时不会修改列表中的原对象。
        _paths[SelectedOs] = Path;
    }

    partial void OnSelectedOsChanged(string value)
    {
        Path = _paths.GetValueOrDefault(value) ?? string.Empty;
        OnPropertyChanged(nameof(CanBrowse));
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (!CanBrowse) return;
        try
        {
            string? picked = await _interaction.PickEditorExecutableAsync();
            if (!string.IsNullOrWhiteSpace(picked))
            {
                if (OperatingSystem.IsMacOS() && System.IO.Directory.Exists(picked) && !picked.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                {
                    ErrorMessage = "请选择编辑器的 .app 应用，也可以手动填写命令或可执行文件路径。";
                    return;
                }
                Path = picked.TrimEnd('/');
            }
            ErrorMessage = string.Empty;
        }
        catch (Exception)
        {
            ErrorMessage = "无法打开应用选择器，请手动填写命令或路径。";
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Path))
        {
            ErrorMessage = "请填写编辑器名称和所选操作系统的命令或路径。";
            return;
        }
        if (!SupportedOsList.Contains(SelectedOs))
        {
            ErrorMessage = "请选择有效的操作系统类型。";
            return;
        }

        _paths[SelectedOs] = Path.Trim();
        Result = new ExternalEditor
        {
            Id = _source.Id,
            Name = Name.Trim(),
            IsDefault = IsDefault,
            ArgumentsTemplate = _source.ArgumentsTemplate,
            CreatedAt = _source.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
            Paths = _paths.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p =>
                new ExternalEditorPath(_source.Paths.FirstOrDefault(old => string.Equals(old.Os, p.Key, StringComparison.OrdinalIgnoreCase))?.Id ?? Guid.NewGuid(),
                    _source.Id, p.Key, p.Value.Trim())).ToList()
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
