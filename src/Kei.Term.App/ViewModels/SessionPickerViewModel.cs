using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;

namespace Kei.Term.App.ViewModels;

// 选择会话对话框。排除规则只用 JumpHostPicker，不另写一套成环/超层判断。
public partial class SessionPickerViewModel : ObservableObject
{
    private readonly IReadOnlyList<TreeNodeBase> _snapshot;
    private readonly HashSet<Guid> _selectable;

    public SessionPickerViewModel(
        IReadOnlyList<TreeNodeBase> roots,
        Guid editingId,
        IReadOnlyList<SessionNode> sessions)
    {
        _snapshot = roots.Select(Clone).ToList();
        _selectable = JumpHostPicker.Selectable(editingId, sessions).Select(s => s.Id).ToHashSet();
        ApplyFilter();
    }

    public string Title { get; } = Strings.Get("SessionPicker.Title");

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<TreeNodeBase> _roots = [];

    [ObservableProperty]
    private TreeNodeBase? _selectedNode;

    public bool IsConfirmed { get; private set; }
    public SessionNode? Result => IsConfirmed ? SelectedNode as SessionNode : null;

    public bool CanConfirm => SelectedNode is SessionNode session && IsSelectable(session);

    public bool IsSelectable(TreeNodeBase node)
        => node is SessionNode session && _selectable.Contains(session.Id);

    public event Action? RequestClose;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnSelectedNodeChanged(TreeNodeBase? value) => OnPropertyChanged(nameof(CanConfirm));

    [RelayCommand]
    private void Confirm()
    {
        if (!CanConfirm)
        {
            return;
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

    // 双击：文件夹展开/收起，可选会话等于确定
    public void Activate(TreeNodeBase? node)
    {
        if (node is FolderNode folder)
        {
            folder.IsExpanded = !folder.IsExpanded;
            return;
        }

        if (node is SessionNode && IsSelectable(node))
        {
            SelectedNode = node;
            Confirm();
        }
    }

    private void ApplyFilter()
    {
        IReadOnlyList<TreeNodeBase> visible = string.IsNullOrWhiteSpace(FilterText)
            ? _snapshot.Select(Clone).ToList()
            : FilterNodes(_snapshot, FilterText.Trim());
        Roots = new ObservableCollection<TreeNodeBase>(visible);
    }

    // 只匹配名称。命中子孙的文件夹保留并展开，不改调用方传入的快照。
    private static List<TreeNodeBase> FilterNodes(IReadOnlyList<TreeNodeBase> nodes, string query)
    {
        var result = new List<TreeNodeBase>();
        foreach (TreeNodeBase node in nodes)
        {
            if (node is FolderNode folder)
            {
                List<TreeNodeBase> children = FilterNodes(folder.Children, query);
                bool self = folder.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (!self && children.Count == 0)
                {
                    continue;
                }

                var copy = (FolderNode)Clone(folder);
                copy.Children = self ? folder.Children.Select(Clone).ToList() : children;
                copy.IsExpanded = true;
                result.Add(copy);
                continue;
            }

            if (node.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(Clone(node));
            }
        }

        return result;
    }

    private static TreeNodeBase Clone(TreeNodeBase node)
    {
        if (node is FolderNode folder)
        {
            return new FolderNode
            {
                Id = folder.Id,
                ParentId = folder.ParentId,
                Name = folder.Name,
                Description = folder.Description,
                SortOrder = folder.SortOrder,
                IsExpanded = folder.IsExpanded,
                Children = folder.Children.Select(Clone).ToList()
            };
        }

        if (node is SessionNode session)
        {
            return new SessionNode
            {
                Id = session.Id,
                ParentId = session.ParentId,
                Name = session.Name,
                Description = session.Description,
                SortOrder = session.SortOrder,
                Host = session.Host,
                Port = session.Port,
                Username = session.Username,
                JumpHostSessionId = session.JumpHostSessionId,
                ProxyProfileId = session.ProxyProfileId
            };
        }

        if (node is VirtualRootNode)
        {
            var copy = new VirtualRootNode
            {
                Name = node.Name,
                IsExpanded = node.IsExpanded
            };
            if (node is VirtualRootNode root)
            {
                copy.Children = root.Children.Select(Clone).ToList();
            }

            return copy;
        }

        return node;
    }
}
