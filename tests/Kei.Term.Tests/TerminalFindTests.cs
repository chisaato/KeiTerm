using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Kei.Term.App.ViewModels;

namespace Kei.Term.Tests;

public class TerminalFindTests
{
    [Fact]
    public Task FindCommands_NavigateMatchesAndCloseWithoutChangingOutput() => HeadlessAvalonia.RunAsync(async () =>
    {
        TerminalTabViewModel tab = new("search", "DejaVu Sans Mono", 14);
        Window window = new() { Width = 600, Height = 300, Content = tab.Terminal };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            tab.Terminal.WriteOutput("needle alpha needle\r\nbeta needle"u8);
            HeadlessAvalonia.Pump();
            var output = tab.Terminal.Screen!.GetViewportRow(0).ReadOnlyCells.ToArray().Select(cell => cell.Codepoint).ToArray();
            Assert.Contains((int)'n', output);

            tab.OpenFindCommand.Execute(null);
            tab.FindQuery = "needle";
            tab.FindNextCommand.Execute(null);
            Assert.True(tab.IsFindBarOpen);
            Assert.Equal(3, tab.Terminal.SearchTotal);
            int first = tab.Terminal.SearchSelected;
            tab.FindNextCommand.Execute(null);
            Assert.NotEqual(first, tab.Terminal.SearchSelected);
            tab.FindPreviousCommand.Execute(null);
            Assert.Equal(first, tab.Terminal.SearchSelected);

            tab.FindQuery = "missing";
            tab.FindNextCommand.Execute(null);
            Assert.NotEmpty(tab.FindStatus);
            tab.CloseFindCommand.Execute(null);
            Assert.False(tab.IsFindBarOpen);
            Assert.Empty(tab.FindStatus);
            Assert.Equal(0, tab.Terminal.SearchTotal);
            Assert.Equal(output, tab.Terminal.Screen.GetViewportRow(0).ReadOnlyCells.ToArray().Select(cell => cell.Codepoint).ToArray());
        }
        finally
        {
            window.Close();
            await tab.DisposeAsync();
        }
    });
}
