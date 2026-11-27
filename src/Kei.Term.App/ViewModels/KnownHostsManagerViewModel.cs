using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.Core.Security;
using Kei.Term.Core.Storage;

namespace Kei.Term.App.ViewModels;

// 已知主机管理：浏览 / 筛选 / 删除 / 吊销与恢复 / 导入导出 OpenSSH known_hosts
public partial class KnownHostsManagerViewModel : ViewModelBase
{
    private readonly IKnownHostRepository _repository;
    private List<KnownHostRowViewModel> _all = [];

    [ObservableProperty]
    private ObservableCollection<KnownHostRowViewModel> _entries = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRevokeCommand))]
    private KnownHostRowViewModel? _selectedEntry;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    // 最近一次导入/导出的结果提示
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public KnownHostsManagerViewModel(IKnownHostRepository repository)
    {
        _repository = repository;
    }

    // 导入源路径；默认 ~/.ssh/known_hosts（测试可替换）
    public Func<string> ImportPathProvider { get; set; } = () => OpenSshPaths.UserKnownHosts;

    // 导出目标由窗口层弹保存框提供；返回 null 表示取消
    public Func<Task<string?>>? PickExportPathAsync { get; set; }

    internal IInteractionService? Interaction { get; set; }

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _isFilterEmpty;

    private bool CanExport() => _all.Count > 0;

    public async Task LoadAsync()
    {
        try
        {
            IReadOnlyList<KnownHostEntry> entries = await _repository.GetAllAsync();
            _all = entries.Select(e => new KnownHostRowViewModel(e)).ToList();
            ApplyFilter();
            ExportCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format("KnownHosts.Error.LoadFailed", ex.Message);
        }
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Guid? selectedId = SelectedEntry?.Entry.Id;
        string filter = FilterText.Trim();
        IEnumerable<KnownHostRowViewModel> visible = filter.Length == 0
            ? _all
            : _all.Where(row => row.Matches(filter));
        Entries = new ObservableCollection<KnownHostRowViewModel>(visible);
        if (selectedId != null)
        {
            SelectedEntry = Entries.FirstOrDefault(r => r.Entry.Id == selectedId.Value);
        }
        IsEmpty = _all.Count == 0;
        IsFilterEmpty = !IsEmpty && Entries.Count == 0;
        Summary = string.Format(Strings.Get("KnownHosts.Summary"), _all.Count, Entries.Count);
    }

    [RelayCommand]
    private void ClearFilter() => FilterText = string.Empty;

    private bool HasSelection() => SelectedEntry != null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var target = SelectedEntry;
        if (target == null || Interaction == null)
        {
            return;
        }

        try
        {
            if (!await Interaction.ConfirmDeleteAsync(target.HostDisplay)) return;
            await _repository.DeleteAsync(target.Entry.Id);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format("KnownHosts.Error.DeleteFailed", ex.Message);
        }
    }

    // 吊销 ⇄ 恢复信任：按 (host, port, key) 幂等写回
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ToggleRevokeAsync()
    {
        if (SelectedEntry == null)
        {
            return;
        }

        Guid targetId = SelectedEntry.Entry.Id;
        try
        {
            KnownHostEntry entry = SelectedEntry.Entry;
            entry.Status = entry.Status == KnownHostStatus.Revoked ? KnownHostStatus.Trusted : KnownHostStatus.Revoked;
            await _repository.SaveAsync(entry);
            await LoadAsync();
            SelectedEntry = Entries.FirstOrDefault(r => r.Entry.Id == targetId);
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format("KnownHosts.Error.UpdateStatusFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        try
        {
            string path = ImportPathProvider();
            if (!File.Exists(path))
            {
                StatusMessage = string.Format(Strings.Get("KnownHosts.ImportMissing"), path);
                return;
            }

            KnownHostsParseResult parsed = OpenSshKnownHostsParser.Parse(await File.ReadAllTextAsync(path));
            foreach (KnownHostEntry entry in parsed.Entries)
            {
                await _repository.SaveAsync(entry);
            }

            StatusMessage = string.Format(Strings.Get("KnownHosts.ImportResult"), parsed.Entries.Count, parsed.SkippedLines);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format("KnownHosts.Error.ImportFailed", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        try
        {
            string? path = PickExportPathAsync == null ? null : await PickExportPathAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            IReadOnlyList<KnownHostEntry> entries = await _repository.GetAllAsync();
            await File.WriteAllTextAsync(path, OpenSshKnownHostsWriter.Write(entries));
            StatusMessage = string.Format(Strings.Get("KnownHosts.ExportResult"), entries.Count, path);
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format("KnownHosts.Error.ExportFailed", ex.Message);
        }
    }
}

// 列表行：只做展示格式化，操作一律回到 Entry
public sealed class KnownHostRowViewModel
{
    public KnownHostRowViewModel(KnownHostEntry entry)
    {
        Entry = entry;
    }

    public KnownHostEntry Entry { get; }

    // 哈希主机名不可逆，只能提示其为哈希条目；通配符等模式原样展示
    public string HostDisplay => Entry.IsPattern
        ? Entry.Host.StartsWith("|1|", StringComparison.Ordinal) ? Strings.Get("KnownHosts.HashedHost") : Entry.Host
        : KnownHostMatcher.ToHostString(Entry.Host, Entry.Port);

    public string KeyType => Entry.KeyType;

    public string Fingerprint => Entry.FingerprintSha256;

    public bool IsRevoked => Entry.Status == KnownHostStatus.Revoked;

    public string StatusText => Strings.Get(IsRevoked ? "KnownHosts.Status.Revoked" : "KnownHosts.Status.Trusted");

    public string SourceText => Strings.Get($"KnownHosts.Source.{Entry.Source}");

    public string LastSeenText => Entry.LastSeenAt is { } seen
        ? seen.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : Strings.Get("KnownHosts.Never");

    public string? Comment => Entry.Comment;

    public bool Matches(string filter)
        => HostDisplay.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || Fingerprint.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || KeyType.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || StatusText.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || SourceText.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || (Comment?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
}
