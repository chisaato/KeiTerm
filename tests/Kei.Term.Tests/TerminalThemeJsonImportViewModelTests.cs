using System.Linq;
using System.Text.Json;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Services;

namespace Kei.Term.Tests;

public sealed class TerminalThemeJsonImportViewModelTests
{
    [Fact]
    public void Format_PreservesUnknownAndDuplicateFields_AndKeepsImportDisabled()
    {
        string json = TerminalThemeJsonParser.ExampleJson.TrimEnd();
        json = json[..^1] + ",\"unexpected\":{\"keep\":true},\"schemaVersion\":1}";
        TerminalThemeJsonImportViewModel draft = new() { JsonText = json };

        draft.FormatCommand.Execute(null);

        using JsonDocument formatted = JsonDocument.Parse(draft.JsonText);
        Assert.True(formatted.RootElement.GetProperty("unexpected").GetProperty("keep").GetBoolean());
        Assert.Equal(2, formatted.RootElement.EnumerateObject().Count(property => property.Name == "schemaVersion"));
        Assert.True(draft.HasErrors);
        Assert.Contains(draft.Issues, issue => issue.Path.EndsWith("unexpected"));
        Assert.Contains(draft.Issues, issue => issue.Path.EndsWith("schemaVersion"));
        Assert.False(draft.CanImport);
        draft.Confirm();
        Assert.Null(draft.Result);
    }

    [Fact]
    public void SyntaxError_DoesNotRewriteInput_AndHasLocation()
    {
        const string json = "{\n  \"schemaVersion\": 1,\n  \"colors\": }";
        TerminalThemeJsonImportViewModel draft = new() { JsonText = json };

        draft.FormatCommand.Execute(null);

        Assert.Equal(json, draft.JsonText);
        Assert.True(Assert.Single(draft.Issues).HasLocation);
        Assert.False(draft.CanImport);
        Assert.Null(draft.PreviewProfile);
    }

    [Fact]
    public void EditedJson_InvalidatesApproval_AndUsesNewColorsAfterValidation()
    {
        TerminalThemeJsonImportViewModel draft = new();
        draft.LoadExampleCommand.Execute(null);
        Assert.True(draft.CanImport);
        string originalBackground = draft.PreviewProfile!.Background;

        draft.JsonText = draft.JsonText.Replace(originalBackground, "#123456");

        Assert.False(draft.CanImport);
        Assert.Null(draft.PreviewProfile);
        draft.ValidateCommand.Execute(null);
        Assert.True(draft.CanImport);
        Assert.Equal("#123456", draft.PreviewProfile!.Background);
        draft.Confirm();
        Assert.Equal("#123456", draft.Result!.Background);
    }

    [Fact]
    public void NameOverride_RequiresFreshValidation_AndCancelDiscardsConfirmedDraft()
    {
        TerminalThemeJsonImportViewModel draft = new();
        draft.LoadExampleCommand.Execute(null);
        string originalName = draft.PreviewProfile!.Name;
        draft.Name = "  Adapted theme  ";
        Assert.False(draft.CanImport);

        draft.ValidateCommand.Execute(null);
        Assert.True(draft.CanImport);
        Assert.Equal("Adapted theme", draft.PreviewProfile!.Name);
        using JsonDocument original = JsonDocument.Parse(draft.JsonText);
        Assert.Equal(originalName, original.RootElement.GetProperty("name").GetString());
        draft.Confirm();
        Assert.Equal("Adapted theme", draft.Result!.Name);
        Assert.NotSame(draft.Result, draft.PreviewProfile);
        draft.Cancel();
        Assert.Null(draft.Result);
    }
}
