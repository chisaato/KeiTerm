using System.Threading.Tasks;
using Avalonia.Media;
using Kei.Term.App.Converters;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models.Profiles;
using Xunit;

namespace Kei.Term.Tests;

public class TabBarAndConnectionStateTests
{
    // ReportError 会向真实终端控件写入红字：须在 Headless UI 线程上运行
    [Fact]
    public Task TerminalTabViewModel_StateTransitions_MaintainCompatibility() => HeadlessAvalonia.RunAsync(() =>
    {
        var tab = new TerminalTabViewModel("TestTab", "monospace", 14.0);

        Assert.Equal(ConnectionState.Connecting, tab.State);
#pragma warning disable CS0618
        Assert.Equal(ConnectionState.Connecting, tab.Status);
#pragma warning restore CS0618

        tab.MarkConnected();
        Assert.Equal(ConnectionState.Connected, tab.State);
#pragma warning disable CS0618
        Assert.Equal(ConnectionState.Connected, tab.Status);
#pragma warning restore CS0618

        tab.ReportError("Network timeout");
        Assert.Equal(ConnectionState.Error, tab.State);
#pragma warning disable CS0618
        Assert.Equal(ConnectionState.Error, tab.Status);
#pragma warning restore CS0618
        Assert.Equal("Network timeout", tab.StatusMessage);
    });

}
