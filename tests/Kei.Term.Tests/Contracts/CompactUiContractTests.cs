namespace Kei.Term.Tests.Contracts;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

    private static string Describe(string file, XObject node, string reason)
        => $"{Path.GetFileName(file)}:{((IXmlLineInfo)node).LineNumber} {reason}";

}
