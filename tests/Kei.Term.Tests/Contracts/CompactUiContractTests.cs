namespace Kei.Term.Tests.Contracts;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

// XAML 契约检查用于防止视图绕过统一设计令牌；终端字形和图标不按正文规则限制。
public class CompactUiContractTests
{
    [Fact]
    public void Views_DoNotHardcodeColors_OrUndersizeBodyText()
    {
        string root = TestRepository.RepositoryRoot;
        HashSet<string> paintProperties = ["Background", "Foreground", "BorderBrush", "Fill", "Stroke"];
        HashSet<string> glyphs = ["«", "»", "↑", "↓", "✕", ".*"];
        List<string> violations = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src/Kei.Term.App/Views"), "*.axaml", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants())
            {
                foreach (XAttribute attribute in element.Attributes())
                {
                    if (paintProperties.Contains(attribute.Name.LocalName) && attribute.Value.StartsWith('#'))
                        violations.Add(Describe(file, attribute, "裸色号"));
                }
                if (element.Name.LocalName == "Setter" && paintProperties.Contains((string?)element.Attribute("Property") ?? "")
                    && ((string?)element.Attribute("Value"))?.StartsWith('#') == true)
                    violations.Add(Describe(file, element, "样式裸色号"));
                if (element.Name.LocalName is "TextBlock" or "SelectableTextBlock"
                    && !glyphs.Contains((string?)element.Attribute("Text") ?? "")
                    && double.TryParse((string?)element.Attribute("FontSize"), CultureInfo.InvariantCulture, out double size)
                    && size < 12)
                    violations.Add(Describe(file, element, $"正文字号 {size}"));
            }
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Views_OnlyReferenceDefinedKeiResources()
    {
        string root = TestRepository.RepositoryRoot;
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        HashSet<string> definedKeys = new(StringComparer.Ordinal);

        string appDir = Path.Combine(root, "src/Kei.Term.App");
        IEnumerable<string> axamlFiles = Directory.EnumerateFiles(appDir, "*.axaml", SearchOption.AllDirectories);
        foreach (string file in axamlFiles)
        {
            XDocument document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants())
            {
                string? key = (string?)element.Attribute(xNs + "Key");
                if (!string.IsNullOrEmpty(key) && key.StartsWith("Kei.", StringComparison.Ordinal))
                {
                    definedKeys.Add(key);
                }
            }
        }

        Regex csharpKeyRegex = new("\"(Kei\\.[A-Za-z0-9_.]+)\"", RegexOptions.Compiled);
        foreach (string csDir in new[] { Path.Combine(appDir, "Services"), Path.Combine(appDir, "DesignSystem") })
        {
            if (!Directory.Exists(csDir)) continue;
            foreach (string file in Directory.EnumerateFiles(csDir, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                foreach (Match match in csharpKeyRegex.Matches(text))
                {
                    definedKeys.Add(match.Groups[1].Value);
                }
            }
        }

        Regex refRegex = new(@"\{(?:DynamicResource|StaticResource)\s+(Kei\.[A-Za-z0-9_.]+)\}", RegexOptions.Compiled);
        List<string> missing = [];
        foreach (string scanDir in new[] { Path.Combine(appDir, "Views"), Path.Combine(appDir, "DesignSystem") })
        {
            foreach (string file in Directory.EnumerateFiles(scanDir, "*.axaml", SearchOption.AllDirectories))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (Match match in refRegex.Matches(lines[i]))
                    {
                        string key = match.Groups[1].Value;
                        if (!definedKeys.Contains(key))
                        {
                            missing.Add($"{Path.GetFileName(file)}:{i + 1} 未定义资源 {key}");
                        }
                    }
                }
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void StringsResx_HasMatchingKeys_AndPreservesEngineWording()
    {
        string root = TestRepository.RepositoryRoot;
        string zhPath = Path.Combine(root, "src/Kei.Term.App/Resources/Strings.resx");
        string enPath = Path.Combine(root, "src/Kei.Term.App/Resources/Strings.en.resx");

        Dictionary<string, string> zh = LoadResx(zhPath);
        Dictionary<string, string> en = LoadResx(enPath);

        string[] requiredKeys = ["Common.None", "Common.ViewArrow", "Settings.Transport.Engine.BuiltIn", "Settings.Transport.Engine.GoCore"];
        foreach (string key in requiredKeys)
        {
            Assert.True(zh.ContainsKey(key), $"Strings.resx 缺少键 {key}");
            Assert.True(en.ContainsKey(key), $"Strings.en.resx 缺少键 {key}");
        }

        Assert.Contains(".NET", zh["Settings.Transport.Engine.BuiltIn"], StringComparison.Ordinal);
        Assert.Contains(".NET", en["Settings.Transport.Engine.BuiltIn"], StringComparison.Ordinal);
        Assert.Contains("Go", zh["Settings.Transport.Engine.GoCore"], StringComparison.Ordinal);
        Assert.Contains("Go", en["Settings.Transport.Engine.GoCore"], StringComparison.Ordinal);

        List<string> missingInZh = en.Keys.Where(k => !zh.ContainsKey(k)).Select(k => $"Strings.resx 缺少 {k}").ToList();
        Assert.True(missingInZh.Count == 0, string.Join(Environment.NewLine, missingInZh));
    }

    private static Dictionary<string, string> LoadResx(string path)
    {
        XDocument doc = XDocument.Load(path);
        return doc.Root!.Elements("data")
            .Where(e => e.Attribute("name") != null)
            .ToDictionary(
                e => (string)e.Attribute("name")!,
                e => (string?)e.Element("value") ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static string Describe(string file, XObject node, string reason)
        => $"{Path.GetFileName(file)}:{((IXmlLineInfo)node).LineNumber} {reason}";

}
