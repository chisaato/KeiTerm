using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.App.Terminals;
using Kei.Term.Core.Fonts;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.ViewModels.Settings;

public sealed partial class FallbackFontEntry : ObservableObject
{
    private readonly AppearanceSettingsPage _parent;
    private readonly FontFamilyOption _option;

    public FallbackFontEntry(AppearanceSettingsPage parent, FallbackFontSetting setting)
    {
        _parent = parent;
        Family = setting.Family;
        _option = new FontFamilyOption(Family, SystemFontScanner.IsRecommendedMonospace(Family));
        IsMissing = TerminalTypefaceProvider.Match(Family, 400) is null;
        AvailableWeights.Add(new FontWeightOption(0, Strings.Get("Settings.Appearance.FallbackWeightFollowPrimary")));
        foreach (int weight in SystemFontScanner.GetAvailableWeights(Family))
            AvailableWeights.Add(new FontWeightOption(weight, $"{FontFaceNaming.WeightName(weight)} ({weight})"));
        // 保留异机配置的显式字重，不因当前机器字体缺失而悄悄改写。
        if (setting.Weight is int saved && !AvailableWeights.Any(option => option.Weight == saved))
            AvailableWeights.Add(new FontWeightOption(saved, $"{FontFaceNaming.WeightName(saved)} ({saved})"));
        _selectedWeight = AvailableWeights.First(option => option.Weight == (setting.Weight ?? 0));
        MoveUpCommand = new RelayCommand(() => Move(-1), () => _parent.TerminalFallbackFonts.IndexOf(this) > 0);
        MoveDownCommand = new RelayCommand(() => Move(1), () => _parent.TerminalFallbackFonts.IndexOf(this) is int index && index >= 0 && index < _parent.TerminalFallbackFonts.Count - 1);
        RemoveCommand = new RelayCommand(() => _parent.TerminalFallbackFonts.Remove(this), () => _parent.TerminalFallbackFonts.Contains(this));
    }

    public string Family { get; }
    public string DisplayName => _option.DisplayName;
    public FontFamilyOption FontOption => _option;
    public string? VariantLabel => _option.VariantLabel;
    public bool IsMonospaceRecommended => _option.IsMonospaceRecommended;
    public FontFamily FontFamily => _option.FontFamily;
    public bool IsMissing { get; }
    public ObservableCollection<FontWeightOption> AvailableWeights { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewFontWeight))]
    private FontWeightOption? _selectedWeight;

    public int? Weight => SelectedWeight is { Weight: > 0 } selected ? selected.Weight : null;
    public FontWeight PreviewFontWeight => (FontWeight)(Weight ?? _parent.FontWeight);
    public string Summary => Weight is int weight ? $"{DisplayName} · {FontFaceNaming.WeightName(weight)}" : DisplayName;
    public IRelayCommand MoveUpCommand { get; }
    public IRelayCommand MoveDownCommand { get; }
    public IRelayCommand RemoveCommand { get; }

    internal void RefreshPrimaryWeight() => OnPropertyChanged(nameof(PreviewFontWeight));
    internal void RefreshCommands()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    private void Move(int delta)
    {
        int index = _parent.TerminalFallbackFonts.IndexOf(this);
        int target = index + delta;
        if (index >= 0 && target >= 0 && target < _parent.TerminalFallbackFonts.Count)
            _parent.TerminalFallbackFonts.Move(index, target);
    }
}
