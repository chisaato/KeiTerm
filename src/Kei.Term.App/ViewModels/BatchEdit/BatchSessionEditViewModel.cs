using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;

namespace Kei.Term.App.ViewModels.BatchEdit;

// 批量修改会话：左侧勾选会话，右侧勾选要改的字段并给出新值，一次性写入。
// 字段由 BatchFieldDefinitions 驱动，本类不含任何具体字段的知识。
public partial class BatchSessionEditViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyCollection<TreeNodeBase>, Task> _save;
    private bool _suppressRefresh;

    // 全部会话（按目录路径排序）；FilteredSessions 为当前筛选下可见的子集
    public IReadOnlyList<BatchSessionItem> Sessions { get; }

    public ObservableCollection<BatchSessionItem> FilteredSessions { get; } = [];

    // 「按文件夹勾选」下拉：含其下全部层级的会话
    public IReadOnlyList<BatchFolderOption> Folders { get; }

    public IReadOnlyList<BatchFieldEditorViewModel> Fields { get; }

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private int _selectedCount;

    // 是否发生过写入尝试（成功或失败）：宿主据此重载会话树，丢弃内存中未落盘的修改
    public bool ApplyAttempted { get; private set; }

    // 实际发生变化并已保存的会话数
    public int ChangedCount { get; private set; }

    public event Action? RequestClose;

    public BatchSessionEditViewModel(
        IReadOnlyList<TreeNodeBase> allNodes,
        IEnumerable<Guid> preselectedSessionIds,
        BatchEditContext context,
        Func<IReadOnlyCollection<TreeNodeBase>, Task> save,
        IReadOnlyList<BatchFieldDefinition>? definitions = null)
    {
        _save = save;
        Dictionary<Guid, TreeNodeBase> byId = allNodes.ToDictionary(n => n.Id);
        var preselected = new HashSet<Guid>(preselectedSessionIds);

        Sessions = allNodes
            .OfType<SessionNode>()
            .Select(s => new BatchSessionItem(s, FolderPath(s.ParentId, byId), preselected.Contains(s.Id), OnSessionSelectionChanged))
            .OrderBy(i => i.Path, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(i => i.Node.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Folders = allNodes
            .OfType<FolderNode>()
            .Select(f => new BatchFolderOption(f.Id, JoinPath(FolderPath(f.ParentId, byId), f.Name)))
            .OrderBy(f => f.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _folderDescendants = allNodes.OfType<FolderNode>().ToDictionary(f => f.Id, f => TreeTraversal.DescendantSessionIds(f.Id, allNodes).ToHashSet());

        Fields = (definitions ?? BatchFieldDefinitions.All)
            .Select(d => new BatchFieldEditorViewModel(d, context, OnFieldChanged))
            .ToList();

        ApplyFilter();
        RefreshSummaries();
    }

    private readonly Dictionary<Guid, HashSet<Guid>> _folderDescendants;

    private IReadOnlyList<SessionNode> SelectedSessions => Sessions.Where(s => s.IsSelected).Select(s => s.Node).ToList();

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        string filter = FilterText.Trim();
        FilteredSessions.Clear();
        foreach (BatchSessionItem item in Sessions)
        {
            if (filter.Length == 0 || item.Matches(filter))
            {
                FilteredSessions.Add(item);
            }
        }
    }

    // 只作用于当前筛选可见的会话：先筛选再全选即"按关键字批量勾选"
    [RelayCommand]
    private void SelectAllVisible() => SetSelection(FilteredSessions, true);

    [RelayCommand]
    private void ClearSelection() => SetSelection(Sessions, false);

    [RelayCommand]
    private void SelectFolder(BatchFolderOption? folder)
    {
        if (folder == null || !_folderDescendants.TryGetValue(folder.Id, out HashSet<Guid>? ids))
        {
            return;
        }

        SetSelection(Sessions.Where(s => ids.Contains(s.Node.Id)), true);
    }

    private void SetSelection(IEnumerable<BatchSessionItem> items, bool selected)
    {
        _suppressRefresh = true;
        try
        {
            foreach (BatchSessionItem item in items.ToList())
            {
                item.IsSelected = selected;
            }
        }
        finally
        {
            _suppressRefresh = false;
        }

        RefreshSummaries();
    }

    private void OnSessionSelectionChanged()
    {
        if (!_suppressRefresh)
        {
            RefreshSummaries();
        }
    }

    private void OnFieldChanged() => UpdateStatus();

    private void RefreshSummaries()
    {
        IReadOnlyList<SessionNode> selected = SelectedSessions;
        SelectedCount = selected.Count;
        foreach (BatchFieldEditorViewModel field in Fields)
        {
            field.UpdateSummary(selected);
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        int fieldCount = Fields.Count(f => f.IsEnabled);
        StatusText = Strings.Format("BatchEdit.Status", SelectedCount, fieldCount);
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private bool CanApply() => SelectedCount > 0 && Fields.Any(f => f.IsEnabled);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        IReadOnlyList<SessionNode> selected = SelectedSessions;
        List<SessionBatchChange> changes = Fields
            .Where(f => f.IsEnabled)
            .Select(f => new SessionBatchChange(f.Definition.Field, f.GetValue()))
            .ToList();
        if (selected.Count == 0 || changes.Count == 0)
        {
            return;
        }

        IReadOnlyList<SessionNode> changed = SessionBatchEditor.Apply(selected, changes);
        ApplyAttempted = true;
        try
        {
            if (changed.Count > 0)
            {
                await _save(changed.Cast<TreeNodeBase>().ToList());
            }
        }
        catch (Exception ex)
        {
            // 保存为单事务：失败即全部未落盘；宿主关闭窗口后重载会话树以丢弃内存中的修改
            StatusText = Strings.Format("BatchEdit.SaveFailed", ex.Message);
            return;
        }

        ChangedCount = changed.Count;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();

    private static string FolderPath(Guid? parentId, IReadOnlyDictionary<Guid, TreeNodeBase> byId)
    {
        var names = new List<string>();
        var visited = new HashSet<Guid>();
        while (parentId is { } id && byId.TryGetValue(id, out TreeNodeBase? parent) && visited.Add(id))
        {
            names.Add(parent.Name);
            parentId = parent.ParentId;
        }

        names.Reverse();
        return string.Join(" / ", names);
    }

    private static string JoinPath(string parentPath, string name)
        => parentPath.Length == 0 ? name : $"{parentPath} / {name}";
}

public sealed record BatchFolderOption(Guid Id, string Path)
{
    public override string ToString() => Path;
}

public partial class BatchSessionItem : ObservableObject
{
    private readonly Action _selectionChanged;

    public BatchSessionItem(SessionNode node, string path, bool isSelected, Action selectionChanged)
    {
        Node = node;
        Path = path;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
    }

    public SessionNode Node { get; }

    // 所在文件夹路径（根下为空）
    public string Path { get; }

    public string Target => string.IsNullOrWhiteSpace(Node.Username) ? Node.Host : $"{Node.Username}@{Node.Host}";

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();

    public bool Matches(string filter)
        => Node.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
           || Node.Host.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
           || Path.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
}

public partial class BatchFieldEditorViewModel : ObservableObject
{
    private readonly BatchEditContext _context;
    private readonly Action _changed;

    public BatchFieldEditorViewModel(BatchFieldDefinition definition, BatchEditContext context, Action changed)
    {
        Definition = definition;
        _context = context;
        _changed = changed;
        Choices = definition.Kind == BatchEditorKind.Choice && definition.Choices != null
            ? definition.Choices(context)
            : [];
        _selectedChoice = Choices.Count > 0 ? Choices[0] : null;
    }

    public BatchFieldDefinition Definition { get; }

    public string Label => Definition.Label;

    public string? Hint => Definition.Hint;

    public bool IsChoice => Definition.Kind == BatchEditorKind.Choice;

    public bool IsText => Definition.Kind == BatchEditorKind.Text;

    public IReadOnlyList<BatchChoice> Choices { get; }

    // 勾选即表示"修改此项"；未勾选的字段保持各会话原值
    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private BatchChoice? _selectedChoice;

    [ObservableProperty]
    private string _text = string.Empty;

    // 选中会话上此字段的现状
    [ObservableProperty]
    private string _summary = string.Empty;

    partial void OnIsEnabledChanged(bool value) => _changed();

    // 改了新值却忘记勾选「修改此项」很常见：编辑即视为要修改
    partial void OnSelectedChoiceChanged(BatchChoice? value) => IsEnabled = true;

    partial void OnTextChanged(string value) => IsEnabled = true;

    public object? GetValue() => IsText
        ? (string.IsNullOrWhiteSpace(Text) ? null : Text.Trim())
        : SelectedChoice?.Value;

    public void UpdateSummary(IReadOnlyCollection<SessionNode> selected)
    {
        SessionBatchSummary summary = SessionBatchEditor.Summarize(Definition.Field, selected);
        Summary = summary.Count == 0
            ? string.Empty
            : summary.IsMixed
                ? Strings.Get("BatchEdit.Summary.Mixed")
                : Strings.Format("BatchEdit.Summary.Current", Describe(summary.CommonValue));
    }

    private string Describe(object? value)
    {
        if (IsText)
        {
            return value as string ?? Definition.EmptyText ?? string.Empty;
        }

        return Choices.FirstOrDefault(c => Equals(c.Value, value))?.Label ?? value?.ToString() ?? string.Empty;
    }
}
