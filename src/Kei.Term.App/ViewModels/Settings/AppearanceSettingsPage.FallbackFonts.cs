using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.ViewModels.Settings;

public partial class AppearanceSettingsPage
{
    public ObservableCollection<FallbackFontEntry> TerminalFallbackFonts { get; } = [];
    private readonly HashSet<FallbackFontEntry> _observedFallbackFonts = [];

    [ObservableProperty]
    private FontFamilyOption? _terminalFallbackFontCandidate;

    public string FallbackPreviewText => "中文 日本語 한글 ⚡ ❯ ➜ ✓ ✗ ⮀ ╭─╮";
    public string TerminalFallbackFontsSummary => TerminalFallbackFonts.Count > 0
        ? string.Join(" › ", TerminalFallbackFonts.Select(entry => entry.Summary))
        : Strings.Get("Settings.Common.NoSelection");

    public List<FallbackFontSetting> GetTerminalFallbackFontSettings() => TerminalFallbackFonts
        .Select(entry => new FallbackFontSetting { Family = entry.Family, Weight = entry.Weight }).ToList();

    public void SetTerminalFallbackFonts(IEnumerable<FallbackFontSetting> fonts)
    {
        // 先物化，允许调用方传入当前集合的投影。
        FallbackFontSetting[] settings = fonts.ToArray();
        TerminalFallbackFonts.Clear();
        foreach (FallbackFontSetting font in settings)
            if (!string.IsNullOrWhiteSpace(font.Family) && !TerminalFallbackFonts.Any(entry => string.Equals(entry.Family, font.Family, StringComparison.OrdinalIgnoreCase)))
                TerminalFallbackFonts.Add(new FallbackFontEntry(this, font));
    }

    public void AddTerminalFallbackFont()
    {
        if (TerminalFallbackFontCandidate is { } candidate && !TerminalFallbackFonts.Any(entry => string.Equals(entry.Family, candidate.Name, StringComparison.OrdinalIgnoreCase)))
            TerminalFallbackFonts.Add(new FallbackFontEntry(this, new FallbackFontSetting { Family = candidate.Name }));
    }

    private void OnTerminalFallbackFontsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (FallbackFontEntry entry in _observedFallbackFonts.Where(entry => !TerminalFallbackFonts.Contains(entry)).ToArray())
        {
            entry.PropertyChanged -= OnFallbackEntryChanged;
            _observedFallbackFonts.Remove(entry);
            entry.RefreshCommands();
        }
        foreach (FallbackFontEntry entry in TerminalFallbackFonts)
        {
            if (_observedFallbackFonts.Add(entry)) entry.PropertyChanged += OnFallbackEntryChanged;
            entry.RefreshCommands();
        }
        NotifyFallbackFontsChanged();
    }

    private void OnFallbackEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FallbackFontEntry.SelectedWeight)) NotifyFallbackFontsChanged();
    }

    private void NotifyFallbackFontsChanged()
    {
        OnPropertyChanged(nameof(PreviewFontFamily));
        OnPropertyChanged(nameof(TerminalFallbackFontsSummary));
        NotifyDraftFontSnapshotChanged();
    }
}
