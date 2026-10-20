using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.ViewModels;

// 传输队列表格中一个可调整的列：宽度与显隐同时绑定到表头和每一行的 ColumnDefinition。
public sealed partial class TransferTableColumn : ObservableObject
{
    public const double MaxWidth = 600;

    public string Key { get; }
    public double DefaultWidth { get; }
    public double MinWidth { get; }
    public bool DefaultVisible { get; }

    [ObservableProperty]
    private double _width;

    [ObservableProperty]
    private bool _isVisible;

    public TransferTableColumn(string key, double defaultWidth, double minWidth, bool defaultVisible)
    {
        Key = key;
        DefaultWidth = defaultWidth;
        MinWidth = minWidth;
        DefaultVisible = defaultVisible;
        _width = defaultWidth;
        _isVisible = defaultVisible;
    }

    public double Clamp(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return DefaultWidth;
        }

        return Math.Clamp(value, MinWidth, MaxWidth);
    }

    public void SetWidth(double value) => Width = Clamp(value);

    public void ResetWidth() => Width = DefaultWidth;
}

// 传输队列表格的共享列状态。同一份设置对应同一个实例，所有连接/侧栏/文件标签共用。
public sealed class TransferTableLayout : ObservableObject
{
    // 方向图标列、操作列固定宽度，名称列占剩余空间（最小 NameMinWidth）。
    public const double DirectionWidth = 22;
    public const double ActionsWidth = 50;
    public const double NameMinWidth = 60;
    // 表头 Padding 8+8，行 Margin 4+4 加 Padding 4+4，两者左右占用相同。
    public const double ChromeWidth = 16;

    private static readonly ConditionalWeakTable<ISettingsService, TransferTableLayout> Shared = new();

    private readonly ISettingsService? _settings;

    public TransferTableColumn RemotePath { get; } = new("RemotePath", 140, 60, false);
    public TransferTableColumn Size { get; } = new("Size", 150, 72, true);
    public TransferTableColumn Progress { get; } = new("Progress", 56, 40, true);
    public TransferTableColumn Speed { get; } = new("Speed", 84, 56, true);
    public TransferTableColumn Status { get; } = new("Status", 72, 48, true);

    public IReadOnlyList<TransferTableColumn> Columns { get; }

    public TransferTableLayout(ISettingsService? settings = null)
    {
        _settings = settings;
        Columns = [RemotePath, Size, Progress, Speed, Status];
        LoadFrom(settings?.Current.FileTransfer.TransferTable);
        foreach (TransferTableColumn column in Columns)
        {
            column.PropertyChanged += OnColumnChanged;
        }
    }

    // 同一个设置服务对应同一份列状态；没有设置服务（如部分测试）时各自独立。
    public static TransferTableLayout For(ISettingsService? settings)
        => settings == null ? new TransferTableLayout() : Shared.GetValue(settings, static s => new TransferTableLayout(s));

    public TransferTableColumn? Find(string key)
    {
        foreach (TransferTableColumn column in Columns)
        {
            if (string.Equals(column.Key, key, StringComparison.Ordinal))
            {
                return column;
            }
        }

        return null;
    }

    public void SetWidth(string key, double width) => Find(key)?.SetWidth(width);

    public void ResetWidth(string key) => Find(key)?.ResetWidth();

    // 拖动结束、双击重置、勾选显隐后调用；拖动过程中不写盘。
    public void Commit()
    {
        if (_settings == null)
        {
            return;
        }

        _settings.Current.FileTransfer.TransferTable = ToSettings();
        _ = SaveAsync();
    }

    public TransferTableSettings ToSettings()
    {
        TransferTableSettings result = new();
        foreach (TransferTableColumn column in Columns)
        {
            result.ColumnWidths[column.Key] = column.Width;
            result.ColumnVisibility[column.Key] = column.IsVisible;
        }

        return result;
    }

    public void LoadFrom(TransferTableSettings? settings)
    {
        foreach (TransferTableColumn column in Columns)
        {
            column.IsVisible = settings?.ColumnVisibility != null && settings.ColumnVisibility.TryGetValue(column.Key, out bool visible)
                ? visible
                : column.DefaultVisible;
            column.Width = settings?.ColumnWidths != null && settings.ColumnWidths.TryGetValue(column.Key, out double width)
                ? column.Clamp(width)
                : column.DefaultWidth;
        }
    }

    private async System.Threading.Tasks.Task SaveAsync()
    {
        try
        {
            await _settings!.SaveSettingsAsync(_settings.Current);
        }
        catch (Exception)
        {
            // 列宽属于界面偏好，写盘失败不打扰用户。
        }
    }

    private void OnColumnChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 勾选显隐立即保存；宽度变化由拖动结束时显式 Commit。
        if (e.PropertyName == nameof(TransferTableColumn.IsVisible))
        {
            Commit();
        }
    }
}

// 某一个文件管理器视图里的一列：由共享的存储宽度和该视图的可用宽度算出实际宽度。
public sealed partial class TransferTableColumnView : ObservableObject
{
    public TransferTableColumn Source { get; }

    // 空间不足时被压缩后的实际宽度；空间充足时等于用户设定宽度。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridWidth))]
    private double _effectiveWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridWidth))]
    [NotifyPropertyChangedFor(nameof(GridMinWidth))]
    private bool _isVisible;

    public TransferTableColumnView(TransferTableColumn source)
    {
        Source = source;
        _effectiveWidth = source.Width;
        _isVisible = source.IsVisible;
    }

    // 隐藏时宽度与最小宽度均为 0，表头和行同步让出空间。
    public Avalonia.Controls.GridLength GridWidth => IsVisible ? new Avalonia.Controls.GridLength(EffectiveWidth) : new Avalonia.Controls.GridLength(0);

    public double GridMinWidth => IsVisible ? Source.MinWidth : 0;
}

// 单个文件管理器视图的表格排布：同一份共享列状态，加上本视图自己的可用宽度。
// 空间不足时先压缩各列到最小宽度（名称列始终保留 NameMinWidth），仍放不下才整体水平滚动。
public sealed class TransferTableView : ObservableObject, IDisposable
{
    private double _availableWidth;

    public TransferTableLayout Layout { get; }

    public TransferTableColumnView RemotePath { get; }
    public TransferTableColumnView Size { get; }
    public TransferTableColumnView Progress { get; }
    public TransferTableColumnView Speed { get; }
    public TransferTableColumnView Status { get; }

    public IReadOnlyList<TransferTableColumnView> Columns { get; }

    // 表格内容的最小总宽；大于视口时外层 ScrollViewer 出现水平滚动条。
    public double TableMinWidth { get; private set; }

    // 表格实际宽度：视口足够时撑满视口，否则取最小总宽。必须是有限值，否则星号列会按文件名全长撑开。
    public double ContentWidth => Math.Max(TableMinWidth, _availableWidth);

    public TransferTableView(TransferTableLayout layout)
    {
        Layout = layout;
        RemotePath = new(layout.RemotePath);
        Size = new(layout.Size);
        Progress = new(layout.Progress);
        Speed = new(layout.Speed);
        Status = new(layout.Status);
        Columns = [RemotePath, Size, Progress, Speed, Status];
        foreach (TransferTableColumn column in layout.Columns)
        {
            column.PropertyChanged += OnSharedColumnChanged;
        }

        Recompute();
    }

    public TransferTableColumnView? Find(string key)
    {
        foreach (TransferTableColumnView column in Columns)
        {
            if (string.Equals(column.Source.Key, key, StringComparison.Ordinal))
            {
                return column;
            }
        }

        return null;
    }

    // 视口宽度（含表格左右内边距）；0 表示尚未布局，不做压缩。
    public void SetAvailableWidth(double width)
    {
        if (Math.Abs(width - _availableWidth) < 0.5)
        {
            return;
        }

        _availableWidth = width;
        Recompute();
    }

    public void Dispose()
    {
        foreach (TransferTableColumn column in Layout.Columns)
        {
            column.PropertyChanged -= OnSharedColumnChanged;
        }
    }

    private void OnSharedColumnChanged(object? sender, PropertyChangedEventArgs e) => Recompute();

    private void Recompute()
    {
        double stored = 0;
        double shrinkable = 0;
        foreach (TransferTableColumnView column in Columns)
        {
            column.IsVisible = column.Source.IsVisible;
            if (!column.IsVisible)
            {
                continue;
            }

            stored += column.Source.Width;
            shrinkable += column.Source.Width - column.Source.MinWidth;
        }

        double fixedWidth = TransferTableLayout.DirectionWidth + TransferTableLayout.ActionsWidth
            + TransferTableLayout.NameMinWidth + TransferTableLayout.ChromeWidth;
        double budget = _availableWidth > 0 ? _availableWidth - fixedWidth : stored;
        double excess = Math.Max(0, stored - budget);
        double ratio = shrinkable > 0 ? Math.Min(1, excess / shrinkable) : 0;

        double total = fixedWidth;
        foreach (TransferTableColumnView column in Columns)
        {
            double width = column.Source.Width - (column.Source.Width - column.Source.MinWidth) * ratio;
            column.EffectiveWidth = width;
            if (column.IsVisible)
            {
                total += width;
            }
        }

        TableMinWidth = total;
        OnPropertyChanged(nameof(TableMinWidth));
        OnPropertyChanged(nameof(ContentWidth));
    }
}
