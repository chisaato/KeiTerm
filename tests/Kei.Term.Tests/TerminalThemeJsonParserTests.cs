using System.Text.Json;
using System.Text.Json.Nodes;
using Kei.Term.Core.Services;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.Tests;

public class TerminalThemeJsonParserTests
{
    private static readonly string[] Palette = Enumerable.Range(0, 16).Select(index => $"#{index:X2}a1b2").ToArray();

    private static string ValidJson => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        name = "测试配色",
        colors = new
        {
            background = "#102030",
            foreground = "#abc123",
            cursor = "#f1e2d3",
            selectionBackground = "#80445566",
            ansi = Palette
        }
    });

    [Fact]
    public void Parse_MapsAllColors_NormalizesRgb_AndCreatesIndependentCustomProfiles()
    {
        TerminalThemeJsonParseResult result = TerminalThemeJsonParser.Parse(ValidJson);
        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
        var profile = Assert.IsType<Kei.Term.Core.Models.Profiles.TerminalProfile>(result.Profile);
        Assert.Equal("测试配色", profile.Name);
        Assert.Equal("#102030", profile.Background);
        Assert.Equal("#ABC123", profile.Foreground);
        Assert.Equal("#F1E2D3", profile.CursorColor);
        Assert.Equal("#80445566", profile.SelectionBackground);
        Assert.Equal(Palette.Select(color => color.ToUpperInvariant()), profile.AnsiColors);
        Assert.False(profile.IsBuiltIn);

        var second = TerminalThemeJsonParser.Parse(ValidJson).Profile!;
        Assert.NotEqual(profile.Id, second.Id);
        second.AnsiColors[0] = "#FFFFFF";
        Assert.Equal("#00A1B2", profile.AnsiColors[0]);
    }

    [Fact]
    public void Parse_MissingOptionalColors_DerivesCursorAndSelectionFromImportedPalette()
    {
        JsonObject document = Document();
        JsonObject colors = document["colors"]!.AsObject();
        colors.Remove("cursor");
        colors.Remove("selectionBackground");
        var profile = TerminalThemeJsonParser.Parse(document.ToJsonString()).Profile!;
        Assert.Equal(profile.Foreground, profile.CursorColor);
        Assert.Equal("#5004A1B2", profile.SelectionBackground);
    }

    [Fact]
    public void Parse_MissingRequiredColor_RejectsWithoutProvidingAnImportableProfile()
    {
        foreach (string property in new[] { "background", "foreground", "ansi" })
        {
            JsonObject document = Document();
            document["colors"]!.AsObject().Remove(property);
            TerminalThemeJsonParseResult result = TerminalThemeJsonParser.Parse(document.ToJsonString());
            AssertIssue(result, "$.colors." + property, TerminalThemeJsonError.MissingProperty);
        }
    }

    [Fact]
    public void Parse_InvalidBaseColor_ReportsTheExactField()
    {
        foreach (string? color in new string?[] { "#fff", "#12345G", "red", " #123456", "", null })
        {
            JsonObject document = Document();
            document["colors"]!["foreground"] = color;
            AssertIssue(TerminalThemeJsonParser.Parse(document.ToJsonString()), "$.colors.foreground", TerminalThemeJsonError.InvalidColor);
        }
    }

    [Fact]
    public void Parse_NonStringColor_IsRejected()
    {
        JsonObject document = Document();
        document["colors"]!["cursor"] = 123456;
        AssertIssue(TerminalThemeJsonParser.Parse(document.ToJsonString()), "$.colors.cursor", TerminalThemeJsonError.InvalidColor);
    }

    [Fact]
    public void Parse_AnsiCountMustBeExactlySixteen()
    {
        foreach (int count in new[] { 0, 15, 17 })
        {
            JsonObject document = Document();
            document["colors"]!["ansi"] = JsonSerializer.SerializeToNode(Enumerable.Repeat("#123456", count));
            AssertIssue(TerminalThemeJsonParser.Parse(document.ToJsonString()), "$.colors.ansi", TerminalThemeJsonError.InvalidAnsiColors);
        }
    }

    [Fact]
    public void Parse_InvalidAnsiSlot_ReportsItsIndex()
    {
        JsonObject document = Document();
        document["colors"]!["ansi"]![9] = "#8011223Z";
        AssertIssue(TerminalThemeJsonParser.Parse(document.ToJsonString()), "$.colors.ansi[9]", TerminalThemeJsonError.InvalidColor);
    }

    [Fact]
    public void Parse_AnsiObjectCannotBeTreatedAsAnOrderedPalette()
    {
        JsonObject document = Document();
        document["colors"]!["ansi"] = new JsonObject { ["black"] = "#000000" };
        AssertIssue(TerminalThemeJsonParser.Parse(document.ToJsonString()), "$.colors.ansi", TerminalThemeJsonError.InvalidAnsiColors);
    }

    [Fact]
    public void Parse_CollectsUnknownAndInvalidFields_AndFormattingPreservesTheOriginalData()
    {
        JsonObject document = Document();
        document["fontSize"] = 40;
        document["colors"]!["backgroud"] = "#000000";
        document["colors"]!["foreground"] = "not a color";
        TerminalThemeJsonParseResult result = TerminalThemeJsonParser.Parse(document.ToJsonString());
        AssertIssue(result, "$.fontSize", TerminalThemeJsonError.UnknownProperty);
        AssertIssue(result, "$.colors.backgroud", TerminalThemeJsonError.UnknownProperty);
        AssertIssue(result, "$.colors.foreground", TerminalThemeJsonError.InvalidColor);
        Assert.True(JsonNode.DeepEquals(document, JsonNode.Parse(result.FormattedJson!)));
    }

    [Fact]
    public void Parse_DuplicateProperties_AreRejectedAndNotSilentlyDroppedByFormatting()
    {
        string duplicate = ValidJson.Replace("\"background\":\"#102030\"", "\"background\":\"#000000\",\"background\":\"#102030\"");
        TerminalThemeJsonParseResult result = TerminalThemeJsonParser.Parse(duplicate);
        AssertIssue(result, "$.colors.background", TerminalThemeJsonError.DuplicateProperty);
        using JsonDocument formatted = JsonDocument.Parse(result.FormattedJson!);
        Assert.Equal(2, formatted.RootElement.GetProperty("colors").EnumerateObject().Count(property => property.Name == "background"));
    }

    [Fact]
    public void Parse_UnsupportedSchemaVersion_IsRejected()
    {
        foreach (string version in new[] { "2", "\"1\"", "null", "true", "1.5" })
        {
            string json = ValidJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":" + version);
            AssertIssue(TerminalThemeJsonParser.Parse(json), "$.schemaVersion", TerminalThemeJsonError.UnsupportedSchemaVersion);
        }
    }

    [Fact]
    public void Parse_MissingTopLevelProperty_IsRejected()
    {
        foreach (string property in new[] { "schemaVersion", "colors" })
        {
            JsonObject document = Document();
            document.Remove(property);
            AssertIssue(TerminalThemeJsonParser.Parse(document.ToJsonString()), "$." + property, TerminalThemeJsonError.MissingProperty);
        }
    }

    [Fact]
    public void Parse_RootMustBeAnObject()
    {
        foreach (string json in new[] { "[]", "null", "42" })
            AssertIssue(TerminalThemeJsonParser.Parse(json), "$", TerminalThemeJsonError.ExpectedObject);
    }

    [Fact]
    public void Parse_NameOverrideSupportsRenamingAndMissingJsonNames()
    {
        JsonObject document = Document();
        document.Remove("name");
        var profile = TerminalThemeJsonParser.Parse(document.ToJsonString(), "  自定义名称  ").Profile!;
        Assert.Equal("自定义名称", profile.Name);
        Assert.Equal("重新命名", TerminalThemeJsonParser.Parse(ValidJson, "重新命名").Profile!.Name);
        AssertIssue(TerminalThemeJsonParser.Parse(document.ToJsonString()), "$.name", TerminalThemeJsonError.InvalidName);
    }

    [Fact]
    public void Parse_InvalidNames_AreRejected()
    {
        foreach (string name in new[] { "", "   ", "broken\nname", "name\n", "\tname" })
        {
            JsonObject document = Document();
            document["name"] = name;
            AssertIssue(TerminalThemeJsonParser.Parse(document.ToJsonString()), "$.name", TerminalThemeJsonError.InvalidName);
        }
    }

    [Fact]
    public void Parse_OversizedNameAndNonStringName_AreRejected()
    {
        AssertIssue(TerminalThemeJsonParser.Parse(ValidJson, new string('a', 121)), "$.name", TerminalThemeJsonError.InvalidName);
        JsonObject document = Document();
        document["name"] = 42;
        AssertIssue(TerminalThemeJsonParser.Parse(document.ToJsonString(), "Valid override"), "$.name", TerminalThemeJsonError.InvalidName);
    }

    [Fact]
    public void Parse_SyntaxErrors_ReportOneBasedEditorLocationWithoutReplacingInput()
    {
        TerminalThemeJsonParseResult result = TerminalThemeJsonParser.Parse("{\n\"schemaVersion\": 1,\ninvalid\n}");
        TerminalThemeJsonIssue issue = Assert.Single(result.Issues);
        Assert.Equal(TerminalThemeJsonError.InvalidJson, issue.Error);
        Assert.Equal(3, issue.LineNumber);
        Assert.True(issue.BytePositionInLine >= 1);
        Assert.Null(result.FormattedJson);
        Assert.Null(result.Profile);
    }

    [Fact]
    public void Parse_OnlyAcceptsStrictJson()
    {
        foreach (string json in new[] { "{", "{\"schemaVersion\":1,}", "{/* comment */}" })
            AssertIssue(TerminalThemeJsonParser.Parse(json), "$", TerminalThemeJsonError.InvalidJson);
    }

    [Fact]
    public void Parse_EnforcesUtf8SizeLimit_ForAsciiAndMultibyteText()
    {
        AssertIssue(TerminalThemeJsonParser.Parse(new string(' ', 256 * 1024) + ValidJson), "$", TerminalThemeJsonError.InputTooLarge);
        string multibyte = "{\"name\":\"" + new string('界', 90000) + "\"}";
        Assert.True(multibyte.Length < TerminalThemeJsonParser.MaximumInputBytes);
        AssertIssue(TerminalThemeJsonParser.Parse(multibyte), "$", TerminalThemeJsonError.InputTooLarge);
    }

    [Fact]
    public void Parse_DeeplyNestedInput_IsRejectedAsInvalidJson()
    {
        string nested = new string('[', 17) + "0" + new string(']', 17);
        AssertIssue(TerminalThemeJsonParser.Parse(nested), "$", TerminalThemeJsonError.InvalidJson);
    }

    [Fact]
    public void Parse_ControlCharactersInNameOverride_AreRejected()
    {
        foreach (string name in new[] { "bad\nname", "name\t" })
            AssertIssue(TerminalThemeJsonParser.Parse(ValidJson, name), "$.name", TerminalThemeJsonError.InvalidName);
    }

    [Fact]
    public void CreateLlmPrompt_ContainsImportableExampleAndEscapesUserProvidedNames()
    {
        const string requestedName = "Ocean \"night\" \\ 配色";
        string prompt = TerminalThemeJsonParser.CreateLlmPrompt(requestedName);
        int exampleStart = prompt.IndexOf('{');
        int exampleEnd = prompt.LastIndexOf('}') + 1;
        TerminalThemeJsonParseResult result = TerminalThemeJsonParser.Parse(prompt[exampleStart..exampleEnd]);
        Assert.True(result.IsValid);
        Assert.Equal(requestedName, result.Profile!.Name);
        Assert.Equal(16, result.Profile.AnsiColors.Length);
    }

    private static JsonObject Document() => JsonNode.Parse(ValidJson)!.AsObject();

    [Fact]
    public void Export_RoundTripsTransparentColors_ExpandsShortHex_AndExcludesInternalSettings()
    {
        TerminalProfile profile = TerminalThemeJsonParser.Parse(ValidJson).Profile!;
        profile.Background = "#123";
        profile.Foreground = "#80abcdef";
        profile.CursorColor = "#90123456";
        profile.AnsiColors[4] = "#77112233";
        string json = TerminalThemeJsonParser.Export(profile);
        TerminalThemeJsonParseResult result = TerminalThemeJsonParser.Parse(json);
        Assert.True(result.IsValid);
        Assert.Equal("#112233", result.Profile!.Background);
        Assert.Equal("#80ABCDEF", result.Profile.Foreground);
        Assert.Equal(profile.CursorColor, result.Profile.CursorColor);
        Assert.Equal(profile.AnsiColors[4], result.Profile.AnsiColors[4]);
        Assert.DoesNotContain(profile.Id, json);
        Assert.DoesNotContain("fontFamily", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("#123", profile.Background);
        Assert.Equal("#80abcdef", profile.Foreground);

        JsonObject document = JsonNode.Parse(json)!.AsObject();
        document["colors"]!.AsObject().Remove("selectionBackground");
        Assert.Equal("#50112233", TerminalThemeJsonParser.Parse(document.ToJsonString()).Profile!.SelectionBackground);
    }

    private static void AssertIssue(TerminalThemeJsonParseResult result, string path, TerminalThemeJsonError error)
    {
        Assert.False(result.IsValid);
        Assert.Null(result.Profile);
        Assert.Contains(result.Issues, issue => issue.Path == path && issue.Error == error);
    }
}
