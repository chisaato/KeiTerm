using System;
using System.Collections.Generic;
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
        string root = TestRepository.RepositoryRoot;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src/Kei.Term.App"), "*.axaml", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(file);
            foreach (XElement path in document.Descendants().Where(element => element.Name.LocalName == "Path"
                && element.Attribute("Data") != null))
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
    public void Views_DoNotUseUnicodeGlyphsAsIcons()
    {
        string root = TestRepository.RepositoryRoot;
        Regex glyph = new(@"[\u2190-\u21ff\u2600-\u27bf\ud83c-\ud83e]");
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src/Kei.Term.App/Views"), "*.axaml", SearchOption.AllDirectories))
        {
            string description = Path.GetRelativePath(root, file);
            XDocument view = XDocument.Load(file);
            var literals = view.Descendants()
                .Where(element => element.Name.LocalName is "TextBlock" or "Button")
                .Select(element => element.Name.LocalName == "TextBlock"
                    ? (string?)element.Attribute("Text")
                    : (string?)element.Attribute("Content"))
                .Where(text => !string.IsNullOrEmpty(text) && !text!.StartsWith("{", StringComparison.Ordinal));
            foreach (string? text in literals)
            {
                Assert.False(glyph.IsMatch(text!), $"{description} 的控件文案含符号图标：{text}");
            }
        }
    }

    [Fact]
    public void AllDefinedIcons_AreReferencedInApp()
    {
        string root = TestRepository.RepositoryRoot;
        string appRoot = Path.Combine(root, "src/Kei.Term.App");

        XDocument keiIconsDoc = XDocument.Load(Path.Combine(appRoot, "DesignSystem/KeiIcons.axaml"));
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keiIconKeys = keiIconsDoc.Root!.Elements()
            .Select(e => (string?)e.Attribute(xNs + "Key"))
            .Where(k => k != null && k.StartsWith("Kei.Icon.", StringComparison.Ordinal))
            .Cast<string>()
            .ToList();

        string settingsIconsText = File.ReadAllText(Path.Combine(appRoot, "ViewModels/Settings/SettingsIcons.cs"));
        var settingsIconNames = Regex.Matches(settingsIconsText, @"public\s+const\s+string\s+(\w+)\s*=")
            .Select(m => $"SettingsIcons.{m.Groups[1].Value}")
            .ToList();

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(appRoot, "DesignSystem/KeiIcons.axaml")),
            Path.GetFullPath(Path.Combine(appRoot, "DesignSystem/KeiFontIcons.axaml")),
            Path.GetFullPath(Path.Combine(appRoot, "DesignSystem/LucideIconResources.cs")),
            Path.GetFullPath(Path.Combine(appRoot, "ViewModels/Settings/SettingsIcons.cs")),
        };

        var appContents = Directory.EnumerateFiles(appRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                && !excluded.Contains(Path.GetFullPath(f)))
            .Select(File.ReadAllText)
            .ToList();

        foreach (string key in keiIconKeys)
        {
            Assert.True(appContents.Any(c => c.Contains(key, StringComparison.Ordinal)),
                $"未使用的图标资源：{key}");
        }

        foreach (string name in settingsIconNames)
        {
            Assert.True(appContents.Any(c => c.Contains(name, StringComparison.Ordinal)),
                $"未使用的设置分类图标：{name}");
        }
    }

    [Fact]
    public void DialogActionLabels_DoNotMixTextSymbolsOrEmojiWithVectorIcons()
    {
        string root = TestRepository.RepositoryRoot;
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

}
