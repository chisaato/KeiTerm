using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;

namespace Kei.Term.Tests;

// 真实终端聚焦时的按键路由：应用消费的关闭/标签导航手势必须只触发应用动作、零远端输入；
// 未被应用消费的普通终端输入必须照常发送（防止适配器一刀切吞键）。
public partial class FileManagerWorkspaceTests
{
    [Fact]
    public Task TerminalFocused_CloseTabShortcut_ClosesDocumentAndSendsNothing() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        host.Terminal.Terminal.Focus();
        HeadlessAvalonia.Pump();
        Assert.Same(host.Terminal.Terminal, host.Window.FocusManager!.GetFocusedElement());

        RawInputModifiers modifier = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        host.Window.KeyPress(AppShortcuts.CloseTab.Key, modifier, PhysicalKey.W, "w");
        HeadlessAvalonia.Pump();
        if (host.Model.CloseCurrentWorkspaceTabCommand.ExecutionTask is { } closing) await closing;

        // 关闭当前文件文档（保留终端连接），且 Ctrl+W 不得作为输入发给远端。
        Assert.False(host.Terminal.IsShellVisible);
        Assert.Single(host.Model.WorkspaceTabs);
        Assert.Equal(0, host.Session.SendCount);
    });

    [Fact]
    public Task TerminalFocused_TabNavigationShortcut_SwitchesTabWithoutRemoteInput() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        TerminalTabViewModel other = new("other", InstalledMonospace.Require(), 14);
        host.Model.Workspace.AddTab(other);
        host.Model.SelectWorkspaceTabCommand.Execute(host.Terminal);
        HeadlessAvalonia.Pump();
        host.Terminal.Terminal.Focus();
        HeadlessAvalonia.Pump();
        Assert.Same(host.Terminal.Terminal, host.Window.FocusManager!.GetFocusedElement());

        int index = Array.IndexOf(host.Model.WorkspaceTabs.ToArray(), other);
        Assert.InRange(index, 0, 8);
        RawInputModifiers modifier = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        host.Window.KeyPress((Key)((int)Key.D1 + index), modifier, PhysicalKey.Digit1, null);
        HeadlessAvalonia.Pump();

        Assert.Same(other, host.Model.ActiveWorkspaceTab);
        Assert.Equal(0, host.Session.SendCount);
    });

    [Fact]
    public Task TerminalFocused_CustomNavigationModifier_IsRespected() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        TerminalTabViewModel other = new("other", InstalledMonospace.Require(), 14);
        host.Model.Workspace.AddTab(other);
        host.Model.SelectWorkspaceTabCommand.Execute(host.Terminal);
        host.Model.CurrentSettings.TabNavigationModifier = "Alt+Shift";
        HeadlessAvalonia.Pump();
        host.Terminal.Terminal.Focus();
        HeadlessAvalonia.Pump();

        int index = Array.IndexOf(host.Model.WorkspaceTabs.ToArray(), other);
        Assert.InRange(index, 0, 8);
        // Primary(=Ctrl) 不再是导航手势：不得切换当前标签。
        host.Window.KeyPress((Key)((int)Key.D1 + index), RawInputModifiers.Control, PhysicalKey.Digit1, null);
        HeadlessAvalonia.Pump();
        Assert.Same(host.Terminal, host.Model.ActiveWorkspaceTab);

        // 设置中的 Alt+Shift 生效且不向远端发送。
        int before = host.Session.SendCount;
        host.Window.KeyPress((Key)((int)Key.D1 + index), RawInputModifiers.Alt | RawInputModifiers.Shift, PhysicalKey.Digit1, null);
        HeadlessAvalonia.Pump();
        Assert.Same(other, host.Model.ActiveWorkspaceTab);
        Assert.Equal(before, host.Session.SendCount);
    });

    [Theory]
    [InlineData(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter)]
    [InlineData(Key.C, RawInputModifiers.Control, PhysicalKey.C)]
    [InlineData(Key.U, RawInputModifiers.Control, PhysicalKey.U)]
    [InlineData(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab)]
    [InlineData(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab)]
    public Task TerminalFocused_UnreservedInput_StillReachesRemote(Key key, RawInputModifiers modifiers, PhysicalKey physicalKey)
        => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Host host = await Host.CreateAsync();
        host.Terminal.Terminal.Focus();
        HeadlessAvalonia.Pump();
        Assert.Same(host.Terminal.Terminal, host.Window.FocusManager!.GetFocusedElement());

        int before = host.Session.SendCount;
        host.Window.KeyPress(key, modifiers, physicalKey, null);
        HeadlessAvalonia.Pump();

        Assert.True(host.Session.SendCount > before,
            $"{key} 配合 {modifiers} 应照常发送给远端，不能被快捷键适配器吞掉");
    });
}
