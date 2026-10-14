using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;

namespace Kei.Term.App.ViewModels;

public sealed record DuplicateSessionResult(string Name, string Hostname, int Port);

public partial class DuplicateSessionViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _hostname = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private int _port = 22;

    public bool IsConfirmed { get; private set; }

    public bool CanConfirm => !string.IsNullOrWhiteSpace(Name) &&
                              !string.IsNullOrWhiteSpace(Hostname) &&
                              Port is >= 1 and <= 65535;

    public event Action? RequestClose;

    public DuplicateSessionViewModel(SessionNode source, IEnumerable<TreeNodeBase> siblings)
    {
        ArgumentNullException.ThrowIfNull(source);
        _hostname = source.Host;
        _port = source.Port ?? 22;
        _name = GenerateDefaultName(source.Name, siblings.Select(s => s.Name));
    }

    // 默认名称生成：若源为 router-gdgz，则优先生成 router-gdgz (1)；若已存在则找未使用的最小序号 (2), (3)...
    public static string GenerateDefaultName(string baseName, IEnumerable<string> existingNames)
    {
        var namesSet = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

        // 如果原名末尾已经是 (N)，提取前缀
        Match match = Regex.Match(baseName, @"^(.*?)\s*\((\d+)\)$");
        string prefix = match.Success ? match.Groups[1].Value.TrimEnd() : baseName;

        int index = 1;
        while (true)
        {
            string candidate = $"{prefix} ({index})";
            if (!namesSet.Contains(candidate))
            {
                return candidate;
            }
            index++;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (!CanConfirm) return;
        IsConfirmed = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsConfirmed = false;
        RequestClose?.Invoke();
    }

    public DuplicateSessionResult ToResult() => new(Name.Trim(), Hostname.Trim(), Port);
}
