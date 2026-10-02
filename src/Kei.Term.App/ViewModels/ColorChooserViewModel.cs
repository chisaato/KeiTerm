using System;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Services;

namespace Kei.Term.App.ViewModels;

public sealed partial class ColorChooserViewModel : ObservableObject
{
    // 进程生命周期内共享的 16 个自定义颜色槽位（2x8）
    private static readonly string[] SharedCustomColors = new string[16]
    {
        "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF",
        "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF"
    };

    // 基本色 6x8 (48 色常用纯色 + 灰阶)
    public static readonly string[] PresetBasicColors = new string[48]
    {
        "#FF8080", "#FFFF80", "#80FF80", "#00FF80", "#80FFFF", "#0080FF", "#FF80C0", "#FF80FF",
        "#FF0000", "#FFFF00", "#00FF00", "#00FF40", "#00FFFF", "#0000FF", "#FF0080", "#FF00FF",
        "#C00000", "#C0C000", "#00C000", "#00C060", "#00C0C0", "#0000C0", "#C00060", "#C000C0",
        "#800000", "#808000", "#008000", "#008040", "#008080", "#000080", "#800040", "#800080",
        "#400000", "#404000", "#004000", "#004020", "#004040", "#000040", "#400020", "#400040",
        "#000000", "#242424", "#484848", "#6D6D6D", "#919191", "#B6B6B6", "#DADADA", "#FFFFFF"
    };

    public ObservableCollection<string> BasicColors { get; } = [];
    public ObservableCollection<string> CustomColors { get; } = [];

    // 外部调色联动回调（拖拽、输入时触发），写回 ProfileEditViewModel
    private readonly Action<string>? _onColorChangedLive;

    [ObservableProperty]
    private int _hue; // 0..359

    [ObservableProperty]
    private int _sat = 255; // 0..255

    [ObservableProperty]
    private int _val = 255; // 0..255

    [ObservableProperty]
    private byte _red = 255;

    [ObservableProperty]
    private byte _green = 255;

    [ObservableProperty]
    private byte _blue = 255;

    [ObservableProperty]
    private byte _alpha = 255;

    [ObservableProperty]
    private string _hex = "#FFFFFF";

    [ObservableProperty]
    private IBrush _previewBrush = new SolidColorBrush(Colors.White);

    [ObservableProperty]
    private Color _pureHueColor = Colors.Red; // 用于明度条的基底渲染 (H, 255, 255)

    private bool _isUpdatingInternally;

    public bool IsConfirmed { get; private set; }

    public ColorChooserViewModel(string initialHex, Action<string>? onColorChangedLive = null)
    {
        _onColorChangedLive = onColorChangedLive;

        foreach (string c in PresetBasicColors)
        {
            BasicColors.Add(c);
        }

        foreach (string c in SharedCustomColors)
        {
            CustomColors.Add(c);
        }

        SetFromHexInternal(initialHex);
    }

    [RelayCommand]
    public void SelectColor(string hex)
    {
        SetFromHexInternal(hex);
        NotifyLive();
    }

    [RelayCommand]
    public void AddToCustomColors()
    {
        // 查找第一个空槽位（白色），或者移位覆盖
        int targetIdx = -1;
        for (int i = 0; i < SharedCustomColors.Length; i++)
        {
            if (SharedCustomColors[i] == "#FFFFFF")
            {
                targetIdx = i;
                break;
            }
        }
        if (targetIdx == -1)
        {
            targetIdx = 0; // 默认覆盖第一个
        }

        SharedCustomColors[targetIdx] = Hex;
        CustomColors[targetIdx] = Hex;
    }

    [RelayCommand]
    public void Confirm()
    {
        IsConfirmed = true;
    }

    public void UpdateHsv(int h, int s, int v)
    {
        if (_isUpdatingInternally) return;
        _isUpdatingInternally = true;

        Hue = Math.Clamp(h, 0, 359);
        Sat = Math.Clamp(s, 0, 255);
        Val = Math.Clamp(v, 0, 255);

        var (r, g, b) = TerminalColorMath.HsvToRgb(Hue, Sat, Val);
        Red = r;
        Green = g;
        Blue = b;

        UpdateHexAndPreview();
        _isUpdatingInternally = false;
        NotifyLive();
    }

    public void UpdateRgb(byte r, byte g, byte b)
    {
        if (_isUpdatingInternally) return;
        _isUpdatingInternally = true;

        Red = r;
        Green = g;
        Blue = b;

        var (h, s, v) = TerminalColorMath.RgbToHsv(Red, Green, Blue);
        Hue = h;
        Sat = s;
        Val = v;

        UpdateHexAndPreview();
        _isUpdatingInternally = false;
        NotifyLive();
    }

    partial void OnHueChanged(int value)
    {
        if (_isUpdatingInternally) return;
        UpdateHsv(value, Sat, Val);
    }

    partial void OnSatChanged(int value)
    {
        if (_isUpdatingInternally) return;
        UpdateHsv(Hue, value, Val);
    }

    partial void OnValChanged(int value)
    {
        if (_isUpdatingInternally) return;
        UpdateHsv(Hue, Sat, value);
    }

    partial void OnRedChanged(byte value)
    {
        if (_isUpdatingInternally) return;
        UpdateRgb(value, Green, Blue);
    }

    partial void OnGreenChanged(byte value)
    {
        if (_isUpdatingInternally) return;
        UpdateRgb(Red, value, Blue);
    }

    partial void OnBlueChanged(byte value)
    {
        if (_isUpdatingInternally) return;
        UpdateRgb(Red, Green, value);
    }

    partial void OnAlphaChanged(byte value)
    {
        if (_isUpdatingInternally) return;
        UpdateHexAndPreview();
        NotifyLive();
    }

    partial void OnHexChanged(string value)
    {
        if (_isUpdatingInternally) return;
        if (TerminalColorMath.TryParseHex(value, out byte r, out byte g, out byte b, out byte a))
        {
            _isUpdatingInternally = true;
            Red = r;
            Green = g;
            Blue = b;
            Alpha = a;

            var (h, s, v) = TerminalColorMath.RgbToHsv(r, g, b);
            Hue = h;
            Sat = s;
            Val = v;

            Hex = TerminalColorMath.ToHex(r, g, b, a);
            Color avColor = Color.FromArgb(a, r, g, b);
            PreviewBrush = new SolidColorBrush(avColor);

            var (pr, pg, pb) = TerminalColorMath.HsvToRgb(Hue, 255, 255);
            PureHueColor = Color.FromRgb(pr, pg, pb);

            _isUpdatingInternally = false;
            NotifyLive();
        }
    }

    private void SetFromHexInternal(string hex)
    {
        if (!TerminalColorMath.TryParseHex(hex, out byte r, out byte g, out byte b, out byte a))
        {
            r = 255; g = 255; b = 255; a = 255;
        }

        _isUpdatingInternally = true;
        Red = r;
        Green = g;
        Blue = b;
        Alpha = a;

        var (h, s, v) = TerminalColorMath.RgbToHsv(r, g, b);
        Hue = h;
        Sat = s;
        Val = v;

        UpdateHexAndPreview();
        _isUpdatingInternally = false;
    }

    private void UpdateHexAndPreview()
    {
        Hex = TerminalColorMath.ToHex(Red, Green, Blue, Alpha);
        Color avColor = Color.FromArgb(Alpha, Red, Green, Blue);
        PreviewBrush = new SolidColorBrush(avColor);

        var (pr, pg, pb) = TerminalColorMath.HsvToRgb(Hue, 255, 255);
        PureHueColor = Color.FromRgb(pr, pg, pb);
    }

    private void NotifyLive()
    {
        _onColorChangedLive?.Invoke(Hex);
    }
}
