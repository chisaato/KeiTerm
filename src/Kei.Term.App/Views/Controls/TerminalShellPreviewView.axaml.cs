using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.Core.Models.Profiles;
using RoyalTerminal.Avalonia.Controls;

namespace Kei.Term.App.Views.Controls;

// 真实内嵌 RoyalTerminal TerminalControl 的终端 Demo 预览控件
// 仅写安全、静态的固定 ANSI 彩色样本，不启动 SSH/PTY/外部 shell，不接收/不转发输入
public partial class TerminalShellPreviewView : UserControl
{
    public static readonly StyledProperty<TerminalProfile?> ProfileProperty =
        AvaloniaProperty.Register<TerminalShellPreviewView, TerminalProfile?>(nameof(Profile));

    public static readonly StyledProperty<TerminalFontSnapshot?> FontProperty =
        AvaloniaProperty.Register<TerminalShellPreviewView, TerminalFontSnapshot?>(nameof(Font));

    public TerminalProfile? Profile
    {
        get => GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
    }

    public TerminalFontSnapshot? Font
    {
        get => GetValue(FontProperty);
        set => SetValue(FontProperty, value);
    }

    private TerminalControl? _terminalControl;
    private EventHandler<RoutedEventArgs>? _terminalLoadedHandler;
    private bool _hasWrittenSample;
    private DispatcherTimer? _cursorBlinkTimer;
    private bool _cursorBlinkPhase = true;

    // 清屏与光标复位转义序列：重置 SGR、清空可视区、清空回滚历史(3J)、光标复位至 (1,1)
    private static readonly byte[] ClearScreenSequence = Encoding.UTF8.GetBytes("\x1b[0m\x1b[2J\x1b[3J\x1b[H");

    // 安全的固定 ANSI 演示样本：测试 ANSI 0..15 色条（带可读索引标注）、CJK 混排、prompt、ls -la、git 状态以及光标
    // 主体文本以默认前景色 (\x1b[0m) 呈现，忠实反映当前主题真实的默认前景/背景对比度与字形渲染
    private static readonly byte[] DemoAnsiSample = Encoding.UTF8.GetBytes(
        "\x1b[0m ANSI Palette:\r\n" +
        " \x1b[40m\x1b[39m 0 \x1b[41m\x1b[39m 1 \x1b[42m\x1b[39m 2 \x1b[43m\x1b[39m 3 \x1b[44m\x1b[39m 4 \x1b[45m\x1b[39m 5 \x1b[46m\x1b[39m 6 \x1b[47m\x1b[39m 7 \x1b[0m\r\n" +
        " \x1b[100m\x1b[39m 8 \x1b[101m\x1b[39m 9 \x1b[102m\x1b[39m10 \x1b[103m\x1b[39m11 \x1b[104m\x1b[39m12 \x1b[105m\x1b[39m13 \x1b[106m\x1b[39m14 \x1b[107m\x1b[39m15 \x1b[0m\r\n\r\n" +
        // CJK 与英文混排的等宽字形覆盖样本（含全角标点与谚文，正文使用默认前景色）
        "\x1b[36m[ZH]\x1b[0m 天地玄黄 宇宙洪荒 • 繁星落入深渊，终端静静流淌。\r\n" +
        "\x1b[36m[JA]\x1b[0m いろはにほへと 散りぬるを • 我が世誰ぞ常ならむ\r\n" +
        "\x1b[36m[KO]\x1b[0m 다람쥐 헌 쳇바퀴에 타고파 • 별빛이 흐르는 은하수\r\n" +
        "\x1b[36m[EN]\x1b[0m The quick brown fox jumps over the lazy dog. 1234567890\r\n\r\n" +
        "\x1b[32muser@keiterm\x1b[0m:\x1b[34m~\x1b[0m$ uname -srm\r\n" +
        "\x1b[0mLinux 6.10.0-keiterm x86_64\r\n\r\n" +
        "\x1b[32muser@keiterm\x1b[0m:\x1b[34m~/workspace\x1b[0m$ ls -la --color=auto\r\n" +
        "\x1b[90mdrwxr-xr-x 4 user user 4096 Sep 29 10:00 \x1b[1;34m.\x1b[0m\r\n" +
        "\x1b[90m-rw-r--r-- 1 user user  220 Sep 29 09:30 \x1b[0m.bashrc\r\n" +
        "\x1b[90mdrwxr-xr-x 2 user user 4096 Sep 29 09:35 \x1b[1;34msrc/\x1b[0m\r\n" +
        "\x1b[90m-rwxr-xr-x 1 user user 8192 Sep 29 09:40 \x1b[1;32mbuild.sh*\x1b[0m\r\n" +
        "\x1b[90m-rw-r--r-- 1 user user 1024 Sep 29 09:42 \x1b[31mpackage.tar.gz\x1b[0m\r\n\r\n" +
        "\x1b[32muser@keiterm\x1b[0m:\x1b[34m~/workspace\x1b[0m$ git status -s\r\n" +
        " \x1b[31mM\x1b[0m BuiltInPresets.cs\r\n" +
        "\x1b[32m??\x1b[0m DesignSystem/KeiTokens.axaml\r\n\r\n" +
        "\x1b[32muser@keiterm\x1b[0m:\x1b[34m~/workspace\x1b[0m$ "
    );

    static TerminalShellPreviewView()
    {
        ProfileProperty.Changed.AddClassHandler<TerminalShellPreviewView>((x, _) => x.UpdateThemeAndFont());
        FontProperty.Changed.AddClassHandler<TerminalShellPreviewView>((x, _) => x.UpdateThemeAndFont());
    }

    public TerminalShellPreviewView()
    {
        InitializeComponent();
        AttachedToLogicalTree += OnAttachedToLogicalTree;
        DetachedFromLogicalTree += OnDetachedFromLogicalTree;
    }

    private void OnAttachedToLogicalTree(object? sender, LogicalTreeAttachmentEventArgs e)
    {
        EnsureTerminalControlCreated();
        UpdateThemeAndFont();
    }

    private void OnDetachedFromLogicalTree(object? sender, LogicalTreeAttachmentEventArgs e)
    {
        CleanupTerminalControl();
    }

    private void EnsureTerminalControlCreated()
    {
        if (_terminalControl != null)
        {
            return;
        }

        // 重新进入可视化树时创建新的 TerminalControl，避免复用已脱离宿主的实例
        var terminal = new TerminalControl
        {
            IsHitTestVisible = false, // 纯预览展示，不接收键盘鼠标输入
            Focusable = false,
            FontLinearMetrics = true,
            FontSubpixelPositioning = true
        };

        // 注册 Loaded 钩子：当控件挂载并建立真实 Renderer 后，重新应用选区 alpha 覆盖与重绘
        _terminalLoadedHandler = (sender, e) =>
        {
            if (ReferenceEquals(sender, _terminalControl))
            {
                OnTerminalLoaded();
            }
        };
        terminal.Loaded += _terminalLoadedHandler;

        _terminalControl = terminal;

        var container = this.FindControl<Border>("TerminalContainer");
        if (container != null)
        {
            container.Child = _terminalControl;
        }

        _hasWrittenSample = false;
    }

    private void CleanupTerminalControl()
    {
        StopCursorBlink();

        if (_terminalControl != null)
        {
            if (_terminalLoadedHandler != null)
            {
                _terminalControl.Loaded -= _terminalLoadedHandler;
                _terminalLoadedHandler = null;
            }

            var container = this.FindControl<Border>("TerminalContainer");
            if (container != null)
            {
                container.Child = null;
            }

            // 安全断开引用：不滥用未公开的私有反射释放
            _terminalControl = null;
            _hasWrittenSample = false;
        }
    }

    private void OnTerminalLoaded()
    {
        if (_terminalControl == null)
        {
            return;
        }

        // 控件 Loaded 后 Renderer 建立，必须通过适配器恢复选区 alpha 覆盖（上游默认改写为 0x80）
        if (Profile != null)
        {
            try
            {
                if (!TerminalThemeAdapter.TryReapplySelectionOverride(_terminalControl, Profile))
                {
                    ShowError("终端选区透明度恢复失败：Renderer 尚未初始化就绪");
                    return;
                }

                _terminalControl.InvalidateTerminal();
            }
            catch (Exception ex)
            {
                ShowError($"终端挂载恢复选区色异常: {ex.Message}");
                return;
            }
        }

        // 确保挂载后如果此前尚未灌入测试样本，在此补灌一次
        EnsureSampleWritten();

        // 挂载后 Renderer 就绪，按当前 Font 快照配置启动或同步光标闪烁
        ApplyCursorBlink();
    }

    private void ShowError(string message)
    {
        var overlay = this.FindControl<Border>("ErrorOverlay");
        var msgBlock = this.FindControl<TextBlock>("ErrorMessageText");
        if (overlay != null && msgBlock != null)
        {
            msgBlock.Text = message;
            overlay.IsVisible = true;
        }
    }

    private void ClearError()
    {
        var overlay = this.FindControl<Border>("ErrorOverlay");
        if (overlay != null)
        {
            overlay.IsVisible = false;
        }
    }

    private void UpdateTerminalContainerBackground()
    {
        var container = this.FindControl<Border>("TerminalContainer");
        if (container == null)
        {
            return;
        }

        string bgHex = Profile?.Background
                       ?? BuiltInPresets.GetDefaultTerminalProfile().Background;

        if (Color.TryParse(bgHex, out var color))
        {
            container.Background = new SolidColorBrush(color);
        }
        else if (Color.TryParse(BuiltInPresets.GetDefaultTerminalProfile().Background, out var fallbackColor))
        {
            container.Background = new SolidColorBrush(fallbackColor);
        }
    }

    private void EnsureSampleWritten()
    {
        if (_terminalControl == null || _hasWrittenSample)
        {
            return;
        }

        try
        {
            _terminalControl.WriteOutput(DemoAnsiSample);
            _hasWrittenSample = true;
        }
        catch (Exception ex)
        {
            ShowError($"样本内容渲染异常: {ex.Message}");
        }
    }

    private void UpdateThemeAndFont()
    {
        // 同步容器 letterbox 背景色，与终端底色保持一致，消除边缘空隙色差
        UpdateTerminalContainerBackground();

        if (_terminalControl == null)
        {
            return;
        }

        ClearError();

        // 1. 应用配色：通过统一适配器注入（内部已包含 TryReapplySelectionOverride）
        if (Profile != null)
        {
            try
            {
                TerminalThemeAdapter.Apply(_terminalControl, Profile);
            }
            catch (Exception ex)
            {
                ShowError($"终端主题适配异常: {ex.Message}");
                return;
            }
        }

        // 2. 应用字体快照：使用规范的主字体提取 PrimaryFontFamily（截取首个候选族名）
        if (Font != null)
        {
            try
            {
                string primaryFamily = Font.PrimaryFontFamily;
                if (!string.IsNullOrWhiteSpace(primaryFamily))
                {
                    _terminalControl.FontFamilyName = primaryFamily;
                }
                if (Font.FontSize >= 8.0 && Font.FontSize <= 48.0)
                {
                    _terminalControl.TerminalFontSize = Font.FontSize;
                }
            }
            catch (Exception ex)
            {
                ShowError($"终端字体设置异常: {ex.Message}");
                return;
            }
        }

        // 3. 在主题与字体均成功设置后：若 Profile 存在，写入清屏序列 \x1b[0m\x1b[2J\x1b[3J\x1b[H，重置守卫并准备重放样本，彻底消除历史单元格残余
        if (Profile != null)
        {
            _terminalControl.WriteOutput(ClearScreenSequence);
            _hasWrittenSample = false;
        }

        // 4. 灌入测试样例文本（若因 Profile 变化清屏，此处重置后的守卫将重新灌入最新着色样本）
        EnsureSampleWritten();

        // 4. 应用光标闪烁配置
        ApplyCursorBlink();

        // 5. 请求重绘
        try
        {
            _terminalControl.InvalidateTerminal();
        }
        catch (Exception ex)
        {
            ShowError($"重绘刷新异常: {ex.Message}");
        }
    }

    private void ApplyCursorBlink()
    {
        if (_terminalControl == null)
        {
            return;
        }

        bool shouldBlink = Font?.CursorBlink ?? false;

        if (shouldBlink)
        {
            StartCursorBlink();
        }
        else
        {
            StopCursorBlink();
        }
    }

    private void StartCursorBlink()
    {
        if (_terminalControl?.Renderer == null)
        {
            // Renderer 尚未建立（例如控件未挂载），等待 Loaded 钩子触发启动
            return;
        }

        if (_cursorBlinkTimer == null)
        {
            _cursorBlinkTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(530)
            };
            _cursorBlinkTimer.Tick += OnCursorBlinkTick;
        }

        if (!_cursorBlinkTimer.IsEnabled)
        {
            _cursorBlinkPhase = true;
            ApplyCursorVisibilityToRenderer(_cursorBlinkPhase);
            _cursorBlinkTimer.Start();
        }
    }

    private void StopCursorBlink()
    {
        if (_cursorBlinkTimer != null)
        {
            _cursorBlinkTimer.Stop();
            _cursorBlinkTimer.Tick -= OnCursorBlinkTick;
            _cursorBlinkTimer = null;
        }

        _cursorBlinkPhase = true;
        ApplyCursorVisibilityToRenderer(true);
    }

    private void OnCursorBlinkTick(object? sender, EventArgs e)
    {
        _cursorBlinkPhase = !_cursorBlinkPhase;
        ApplyCursorVisibilityToRenderer(_cursorBlinkPhase);
    }

    private void ApplyCursorVisibilityToRenderer(bool visible)
    {
        if (_terminalControl?.Renderer is { } renderer)
        {
            try
            {
                renderer.CursorVisible = visible;
                _terminalControl.InvalidateTerminal();
            }
            catch (Exception ex)
            {
                ShowError($"光标渲染刷新异常: {ex.Message}");
            }
        }
    }
}
