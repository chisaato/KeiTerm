using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Kei.Term.Tests.Contracts;

public class IconSystemContractTests
{
    [Fact]
    public void LucideViews_UseUnfilledCenterlinesInsideFixedViewports()
    {
        string root = FindSolutionRoot();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src/Kei.Term.App"), "*.axaml", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(file);
            foreach (XElement path in document.Descendants().Where(element => element.Name.LocalName == "Path"
                && (((string?)element.Attribute("Data"))?.Contains("Kei.Icon.") == true
                    || ((string?)element.Attribute("Data"))?.StartsWith("{Binding Icon", StringComparison.Ordinal) == true)))
            {
                string description = Path.GetRelativePath(root, file);
                Assert.Contains("kei-icon", ((string?)path.Attribute("Classes") ?? "").Split(' '));
                Assert.Null(path.Attribute("Fill"));
                Assert.Equal("24", (string?)path.Attribute("Width"));
                Assert.Equal("24", (string?)path.Attribute("Height"));
                Assert.Equal("None", (string?)path.Attribute("Stretch"));
                XElement? canvas = path.Parent;
                Assert.True(canvas?.Name.LocalName == "Grid" && (string?)canvas.Attribute("Width") == "24"
                    && (string?)canvas.Attribute("Height") == "24", $"{description}: 图标缺少固定 24×24 画布");
                Assert.True(canvas!.Parent?.Name.LocalName == "Viewbox", $"{description}: 图标应通过 Viewbox 缩放完整视口");
            }
        }
    }

    [Fact]
    public void DialogActionLabels_DoNotMixTextSymbolsOrEmojiWithVectorIcons()
    {
        string root = FindSolutionRoot();
        XDocument strings = XDocument.Load(Path.Combine(root, "src", "Kei.Term.App", "Resources", "Strings.resx"));
        var labels = strings.Root!.Elements("data").ToDictionary(data => data.Attribute("name")!.Value,
            data => data.Element("value")!.Value);
        Regex glyph = new(@"[\u2190-\u21ff\u2600-\u27bf\ud83c-\ud83e]");
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src", "Kei.Term.App", "Views"), "*Window.axaml"))
        {
            if (Path.GetFileName(file) == "MainWindow.axaml") continue;
            XDocument view = XDocument.Load(file);
            foreach (XElement button in view.Descendants().Where(element => element.Name.LocalName == "Button"))
            {
                var actionLabels = button.Descendants().Where(element => element.Name.LocalName == "TextBlock")
                    .Select(element => (string?)element.Attribute("Text")).Prepend((string?)button.Attribute("Content"))
                    .Where(label => label != null);
                foreach (string? actionLabel in actionLabels)
                {
                    string label = actionLabel!;
                    Match localized = Regex.Match(label, @"^\{loc:KeiString\s+([^}]+)\}$");
                    if (localized.Success) label = labels[localized.Groups[1].Value];
                    Assert.False(glyph.IsMatch(label), $"{Path.GetFileName(file)} 的按钮文案含符号图标：{label}");
                }
            }
        }
    }

    private static string FindSolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !Directory.GetFiles(dir, "*.sln*").Any())
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        return dir ?? Environment.CurrentDirectory;
    }
}
