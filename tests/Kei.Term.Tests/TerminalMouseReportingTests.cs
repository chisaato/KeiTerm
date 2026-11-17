namespace Kei.Term.Tests;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Messaging;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.Terminals;
using Kei.Term.App.ViewModels;
using RoyalTerminal.Terminal;

public class TerminalMouseReportingTests
{
    private sealed class RecordingEndpoint : ITerminalEndpoint
    {
        public List<string> SentSequences { get; } = [];
        public void SendText(ReadOnlySpan<byte> utf8) => SentSequences.Add(Encoding.UTF8.GetString(utf8));
        public void SetSize(int widthPx, int heightPx) { }
        public void SetFocus(bool focused) { }
    }

    [Fact]
    public Task MouseReportingToggle_AndShiftTemporaryFlip_BehaveAsExpected() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        KeiTerminalControl terminal = Assert.IsType<KeiTerminalControl>(
            TerminalControlFactory.Create(new VtCallbackHooks(), mouseReportingEnabled: true));
        RecordingEndpoint endpoint = new();
        Window window = new() { Width = 800, Height = 480, Content = terminal };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            terminal.AttachEndpoint(endpoint);
            terminal.WriteOutput(Encoding.UTF8.GetBytes("hello world line 1\r\nhello world line 2\r\n"));
            // 远端开启 SGR 鼠标上报
            terminal.WriteOutput(Encoding.UTF8.GetBytes("\x1b[?1006h\x1b[?1000h\x1b[?1002h"));
            HeadlessAvalonia.Pump();

            double cw = terminal.Renderer!.CellWidth;
            double ch = terminal.Renderer!.CellHeight;
            Point p1 = new(cw * 1, ch * 0.5);
            Point p2 = new(cw * 8, ch * 0.5);

            // 1. 开关开启 + 无 Shift -> 上报给远端，不建立本地选区
            endpoint.SentSequences.Clear();
            Drag(window, p1, p2, RawInputModifiers.None);
            Assert.False(terminal.HasSelection);
            Assert.Contains(endpoint.SentSequences, s => s.StartsWith("\x1b[<", StringComparison.Ordinal));

            // 2. 开关开启 + Shift -> 临时本地选区，不上报
            endpoint.SentSequences.Clear();
            Drag(window, p1, p2, RawInputModifiers.Shift);
            Assert.True(terminal.HasSelection);
            Assert.DoesNotContain(endpoint.SentSequences, s => s.StartsWith("\x1b[<", StringComparison.Ordinal));

            // 3. 关闭开关 + 无 Shift -> 直接本地选区（清除旧选区后重新选中），不上报
            terminal.IsMouseReportingEnabled = false;
            endpoint.SentSequences.Clear();
            Point p3 = new(cw * 2, ch * 1.5);
            Point p4 = new(cw * 6, ch * 1.5);
            Drag(window, p3, p4, RawInputModifiers.None);
            Assert.True(terminal.HasSelection);
            Assert.DoesNotContain(endpoint.SentSequences, s => s.StartsWith("\x1b[<", StringComparison.Ordinal));
            Assert.Equal(1, terminal.Renderer.SelectionStart?.Row);
            Assert.Equal(1, terminal.Renderer.SelectionEnd?.Row);

            // 4. 关闭开关 + Shift -> 临时翻转，上报鼠标事件给远端
            terminal.ClearSelection();
            endpoint.SentSequences.Clear();
            Drag(window, p1, p2, RawInputModifiers.Shift);
            Assert.False(terminal.HasSelection);
            Assert.Contains(endpoint.SentSequences, s => s.StartsWith("\x1b[<", StringComparison.Ordinal));
        }
        finally
        {
            terminal.DetachEndpoint();
            window.Close();
        }
    });

    [Fact]
    public async Task Messenger_UpdatesOpenTabTerminalMouseReporting()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            UiDesignSystemService.Apply();
            await using var tab = new TerminalTabViewModel(
                "test",
                new TerminalFontSnapshot("monospace", [], 14, true),
                Kei.Term.Core.Models.Profiles.BuiltInPresets.GetDefaultTerminalProfile(),
                explicitProfileId: null,
                mouseReportingEnabled: true);
            KeiTerminalControl terminal = Assert.IsType<KeiTerminalControl>(tab.Terminal);
            Assert.True(terminal.IsMouseReportingEnabled);

            WeakReferenceMessenger.Default.Send(new TerminalMouseReportingChangedMessage(false));
            HeadlessAvalonia.Pump();
            Assert.False(terminal.IsMouseReportingEnabled);

            WeakReferenceMessenger.Default.Send(new TerminalMouseReportingChangedMessage(true));
            HeadlessAvalonia.Pump();
            Assert.True(terminal.IsMouseReportingEnabled);
        });
    }

    private static void Drag(Window window, Point from, Point to, RawInputModifiers modifiers)
    {
        window.MouseDown(from, MouseButton.Left, modifiers);
        window.MouseMove(to, modifiers | RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left, modifiers);
        HeadlessAvalonia.Pump();
    }
}
