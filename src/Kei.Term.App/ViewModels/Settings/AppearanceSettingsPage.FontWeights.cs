using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.App.Services;
using Kei.Term.App.Terminals;
using Kei.Term.Core.Fonts;

namespace Kei.Term.App.ViewModels.Settings;

public sealed record FontWeightOption(int Weight, string DisplayName);

public partial class AppearanceSettingsPage
{
    public ObservableCollection<FontWeightOption> AvailableFontWeights { get; } = [];
    private bool _refreshingWeights;

    [ObservableProperty]
    private FontWeightOption? _selectedFontWeight;

    [ObservableProperty]
    private int _fontWeight = 400;

    public string FontSummary => $"{FontFamily} · {FontFaceNaming.WeightName(FontWeight)} · {FontSize.ToString("0.##", CultureInfo.InvariantCulture)}pt";

    private void RefreshFontWeights()
    {
        _refreshingWeights = true;
        AvailableFontWeights.Clear();
        foreach (int weight in SystemFontScanner.GetAvailableWeights(FontFamily))
            AvailableFontWeights.Add(new FontWeightOption(weight, $"{FontFaceNaming.WeightName(weight)} ({weight})"));
        SelectedFontWeight = FindAvailableWeight(FontWeight);
        _refreshingWeights = false;
        if (SelectedFontWeight is not null) FontWeight = SelectedFontWeight.Weight;
    }

    partial void OnSelectedFontWeightChanged(FontWeightOption? value)
    {
        if (!_refreshingWeights && value is not null) FontWeight = value.Weight;
    }

    private FontWeightOption? FindAvailableWeight(int requested)
    {
        // 设置预览与首次打开的终端使用同一字体匹配规则。系统选择的字面可能与数值最近的字重不同。
        var exact = AvailableFontWeights.FirstOrDefault(option => option.Weight == requested);
        if (exact != null) return exact;
        int weight = Math.Clamp(requested, 100, 900);
        int matched = TerminalTypefaceProvider.Match(FontFamily, weight)?.FontWeight ?? weight;
        return AvailableFontWeights.MinBy(option => Math.Abs(option.Weight - matched));
    }

    partial void OnFontWeightChanged(int value)
    {
        FontWeightOption? nearest = FindAvailableWeight(value);
        int normalized = nearest?.Weight ?? Math.Clamp(value, 100, 900);
        if (value != normalized)
        {
            FontWeight = normalized;
            return;
        }
        SelectedFontWeight = nearest;
        OnPropertyChanged(nameof(FontSummary));
        OnPropertyChanged(nameof(PreviewFontWeight));
        foreach (FallbackFontEntry entry in TerminalFallbackFonts) entry.RefreshPrimaryWeight();
        NotifyDraftFontSnapshotChanged();
    }
}
