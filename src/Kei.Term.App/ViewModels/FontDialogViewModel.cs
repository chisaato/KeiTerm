using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.App.ViewModels;

// 编辑已有 TerminalProfile 的字重和行距。不新建字体模型。
// 取消不写回；确定才改正在编辑的 profile。字号仍留在设置页。
public sealed partial class FontDialogViewModel : ObservableObject
{
    private static readonly string[] KnownWeights = ["Regular", "Normal", "Medium", "SemiBold", "Bold"];

    private readonly TerminalProfile _profile;

    public FontDialogViewModel(TerminalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        FontWeight = string.IsNullOrWhiteSpace(profile.FontWeight) ? "Normal" : profile.FontWeight;
        LineHeight = profile.LineHeight;
        WeightOptions = KnownWeights.Contains(FontWeight, StringComparer.Ordinal)
            ? KnownWeights
            : [FontWeight, .. KnownWeights];
    }

    public IReadOnlyList<string> WeightOptions { get; }

    [ObservableProperty]
    private string _fontWeight;

    [ObservableProperty]
    private double _lineHeight;

    public bool IsConfirmed { get; private set; }

    public void Confirm()
    {
        if (string.IsNullOrWhiteSpace(FontWeight) || LineHeight <= 0)
        {
            return;
        }

        _profile.FontWeight = FontWeight.Trim();
        _profile.LineHeight = LineHeight;
        IsConfirmed = true;
    }

    public void Cancel()
    {
        IsConfirmed = false;
    }
}
