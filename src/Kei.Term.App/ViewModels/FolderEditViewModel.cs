using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;

namespace Kei.Term.App.ViewModels;

public partial class FolderEditViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = Strings.Get("FolderEdit.Title.New");

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    public Guid NodeId { get; }
    public Guid? ParentId { get; set; }
    public bool IsConfirmed { get; private set; }

    public event Action? RequestClose;

    public FolderEditViewModel(FolderNode? existing, Guid? parentId)
    {
        if (existing != null)
        {
            Title = Strings.Get("FolderEdit.Title.Edit");
            NodeId = existing.Id;
            ParentId = existing.ParentId;
            Name = existing.Name;
            Description = existing.Description ?? string.Empty;
        }
        else
        {
            NodeId = Guid.NewGuid();
            ParentId = parentId;
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = Strings.Get("FolderEdit.DefaultName");
        }

        IsConfirmed = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsConfirmed = false;
        RequestClose?.Invoke();
    }

    // 文件夹为纯分类容器，仅维护名称与描述
    public FolderNode ApplyToModel(FolderNode? target = null)
    {
        var model = target ?? new FolderNode { Id = NodeId, ParentId = ParentId };
        model.Name = Name.Trim();
        model.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        return model;
    }
}
