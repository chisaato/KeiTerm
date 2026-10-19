using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.App.Terminals;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views.Controls;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;
using RoyalTerminal.Avalonia.Controls;

namespace Kei.Term.Tests;

public partial class TerminalProfileApplicationTests
{
    [MonospaceFact]
    public Task StartupBeforeSettingsLoad_FirstTerminalMatchesSettingsPreview() => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = NewTempDir();
        string path = Path.Combine(directory, "settings.json");
        AppSettings committed = new() { FontFamily = InstalledMonospace.Require(), FontSize = 11, TerminalFontWeight = 500 };
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(committed));
        JsonSettingsService settings = new(path);
        ProfileManagerService manager = new(settings, directory);
        await using MainViewModel main = CreateMainViewModel(settings, manager);
        // 复刻 App 的启动次序：设置 VM 与主窗口先同步构造，之后才异步读取磁盘。
        using SettingsViewModel editor = new(settings, directory, profileManager: manager);
        await settings.LoadSettingsAsync();
        await manager.InitializeAsync();
        ((IConnectionHost)main).OpenTab(new ResolvedSessionConfig(Guid.NewGuid(), "Initial", "localhost", 22,
            "user", null, "xterm-256color", null, null, new System.Collections.Generic.Dictionary<string, string>()));
        TerminalTabViewModel tab = Assert.Single(main.Tabs);
        TerminalControl terminal = tab.Terminal;
        Window host = new() { Width = 800, Height = 500, Content = terminal };
        host.SetRenderScaling(1.25);
        try
        {
            host.Show();
            HeadlessAvalonia.Pump();
            InitialFontSession session = new();
            tab.AttachSession(session);
            tab.MarkConnected();
            session.Emit("user@host:~$ Hello world\r\n");
            HeadlessAvalonia.Pump();
            var initial = (terminal.Renderer!.FontSize, terminal.Renderer.CellWidth, terminal.Renderer.CellHeight,
                terminal.TerminalFontSize, terminal.Padding, terminal.ScrollData!.CellHeight);
            // WireDialogs 每次打开设置时都会 Reload；这里走同一条真实字体草稿广播链路。
            editor.Reload();
            HeadlessAvalonia.Pump();
            var preview = (terminal.Renderer!.FontSize, terminal.Renderer.CellWidth, terminal.Renderer.CellHeight,
                terminal.TerminalFontSize, terminal.Padding, terminal.ScrollData!.CellHeight);
            Assert.Equal(initial, preview);
            Assert.Equal(committed.FontSize, terminal.TerminalFontSize);
            Assert.Equal(committed.FontFamily, terminal.FontFamilyName);
            Assert.Equal(TerminalTypefaceProvider.Match(committed.FontFamily, committed.TerminalFontWeight)!.FontWeight,
                terminal.TypefaceCollection!.GetPrimaryTypeface().FontWeight);

            // 设置预览使用同一份已加载的草稿，分数缩放下也必须与会话终端保持一致。
            TerminalShellPreviewView shellPreview = new() { Font = AppearanceOf(editor).DraftFontSnapshot, Profile = tab.CurrentProfile };
            Window previewHost = new() { Width = 800, Height = 500, Content = shellPreview };
            previewHost.SetRenderScaling(1.25);
            try
            {
                previewHost.Show();
                HeadlessAvalonia.Pump();
                TerminalControl previewTerminal = Assert.IsType<TerminalControl>(shellPreview.FindControl<Border>("TerminalContainer")!.Child);
                Assert.Equal(terminal.Renderer.FontSize, previewTerminal.Renderer!.FontSize);
                Assert.Equal(terminal.Renderer.CellWidth, previewTerminal.Renderer.CellWidth);
                Assert.Equal(terminal.Renderer.CellHeight, previewTerminal.Renderer.CellHeight);
            }
            finally { previewHost.Close(); }
        }
        finally
        {
            host.Close();
            await tab.DisposeAsync();
            Directory.Delete(directory, true);
        }
    });

    private sealed class InitialFontSession : Kei.Term.Core.Abstractions.ITerminalSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected => true;
        public event Action<byte[]>? OutputReceived;
        public event Action<Exception?>? Disconnected { add { } remove { } }
        public void Emit(string text) => OutputReceived?.Invoke(Encoding.UTF8.GetBytes(text));
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
