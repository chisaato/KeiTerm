using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.App.Services;
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
        SelectedFontWeight = AvailableFontWeights.MinBy(option => Math.Abs(option.Weight - FontWeight));
        _refreshingWeights = false;
        if (SelectedFontWeight is not null) FontWeight = SelectedFontWeight.Weight;
    }

    partial void OnSelectedFontWeightChanged(FontWeightOption? value)
    {
        if (!_refreshingWeights && value is not null) FontWeight = value.Weight;
    }

    partial void OnFontWeightChanged(int value)
    {
        FontWeightOption? nearest = AvailableFontWeights.MinBy(option => Math.Abs((long)option.Weight - value));
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
