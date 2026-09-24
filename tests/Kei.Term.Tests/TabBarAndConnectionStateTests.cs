using Avalonia.Media;
using Kei.Term.App.Converters;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models.Profiles;
using Xunit;

namespace Kei.Term.Tests;

public class TabBarAndConnectionStateTests
{
    [Fact]
    public void TerminalTabViewModel_StateTransitions_MaintainCompatibility()
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
    }

    [Fact]
    public void ConnectionStateBrushConverter_MapsAllStatesGracefully()
    {
        var converter = new ConnectionStateBrushConverter();

        foreach (var state in Enum.GetValues<ConnectionState>())
        {
            var brush = converter.Convert(state, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture);
            Assert.NotNull(brush);
            Assert.IsAssignableFrom<IBrush>(brush);
        }
    }

    [Fact]
    public void TabPlacement_EnumValues_AreDefined()
    {
        Assert.Equal(0, (int)TabPlacement.Top);
        Assert.Equal(1, (int)TabPlacement.Bottom);
    }
}
