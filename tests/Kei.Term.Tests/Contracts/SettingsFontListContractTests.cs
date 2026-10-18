using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Kei.Term.Tests.Contracts;

public class SettingsFontListContractTests
{
    [Fact]
    public void SettingsWindow_AllSixFontLists_RenderWithTheirOwnFontFamily()
    {
        XDocument doc = LoadSettingsPageDocuments();

        // 1. 界面主字体 AutoCompleteBox (SelectedItem="{Binding SelectedUiFont}")
        AssertAutoCompleteBoxFontBinding(doc, "{Binding SelectedUiFont}", "services:FontFamilyOption", "{Binding FontFamily}");

        // 2. 界面回退字体候选 AutoCompleteBox (SelectedItem="{Binding Parent.UiFallbackFontCandidate}")
        AssertAutoCompleteBoxFontBinding(doc, "{Binding Parent.UiFallbackFontCandidate}", "services:FontFamilyOption", "{Binding FontFamily}");

        // 3. 界面已选回退 ListBox (ItemsSource="{Binding Parent.UiFallbackFonts}")
        AssertListBoxFontBinding(doc, "{Binding Parent.UiFallbackFonts}", "x:String", "{Binding}");

        // 4. 终端主等宽字体 AutoCompleteBox (SelectedItem="{Binding SelectedFont}")
        AssertAutoCompleteBoxFontBinding(doc, "{Binding SelectedFont}", "services:FontFamilyOption", "{Binding FontFamily}");

        // 5. 终端回退候选 AutoCompleteBox (SelectedItem="{Binding Parent.TerminalFallbackFontCandidate}")
        AssertAutoCompleteBoxFontBinding(doc, "{Binding Parent.TerminalFallbackFontCandidate}", "services:FontFamilyOption", "{Binding FontFamily}");

        // 6. 终端回退条目用自身字体渲染名称，权重与命令由条目承担。
        XElement terminalFonts = doc.Descendants().Single(element => element.Attribute("ItemsSource")?.Value == "{Binding Parent.TerminalFallbackFonts}");
        XElement template = terminalFonts.Descendants().First(element => element.Name.LocalName == "DataTemplate");
        Assert.Equal("settings:FallbackFontEntry", template.Attributes().Single(attribute => attribute.Name.LocalName == "DataType").Value);
        Assert.Contains(template.Descendants(), element => element.Name.LocalName == "TextBlock"
            && element.Attribute("Text")?.Value == "{Binding DisplayName}"
            && element.Attribute("FontFamily")?.Value == "{Binding FontFamily}");
    }

    private static void AssertAutoCompleteBoxFontBinding(
        XDocument doc,
        string selectedItemBinding,
        string expectedDataType,
        string expectedFontFamilyBinding)
    {
        XElement box = FindAutoCompleteBoxBySelectedItem(doc, selectedItemBinding);

        // 字体候选复用同一模板；沿资源键查找，不能误用其他列表的模板。
        Assert.Contains("settingsFontPicker", box.Attribute("Classes")?.Value);
        Assert.Equal("{StaticResource SettingsFontOptionTemplate}", box.Attribute("ItemTemplate")?.Value);
        XElement dataTemplate = doc.Descendants().Single(element => element.Name.LocalName == "DataTemplate"
            && element.Attributes().Any(attribute => attribute.Name.LocalName == "Key" && attribute.Value == "SettingsFontOptionTemplate"));

        XName dataTypeAttrName = dataTemplate.Name.Namespace + "DataType";
        string? actualDataType = dataTemplate.Attribute(dataTypeAttrName)?.Value
            ?? dataTemplate.Attributes().FirstOrDefault(a => a.Name.LocalName == "DataType")?.Value;
        Assert.Equal(expectedDataType, actualDataType);

        XElement? textBlock = dataTemplate.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "TextBlock");
        Assert.NotNull(textBlock);
        Assert.Equal(expectedFontFamilyBinding, textBlock.Attribute("FontFamily")?.Value);
        Assert.Equal("{Binding DisplayName}", textBlock.Attribute("Text")?.Value);
        Assert.Equal("CharacterEllipsis", textBlock.Attribute("TextTrimming")?.Value);
        Assert.Equal("Grid", dataTemplate.Elements().Single().Name.LocalName);
        // 过滤与选中回写走 DisplayName，不能绑到 FontFamily 对象。
        Assert.Equal("{CompiledBinding DisplayName, DataType=services:FontFamilyOption}", box.Attribute("ValueMemberBinding")?.Value);
    }

    private static void AssertListBoxFontBinding(
        XDocument doc,
        string itemsSourceBinding,
        string expectedDataType,
        string expectedFontFamilyBinding)
    {
        XElement listBox = FindListBoxByItemsSource(doc, itemsSourceBinding);

        // 严格仅在当前 ListBox 的直系后代元素中检索 DataTemplate
        XElement? dataTemplate = listBox.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "DataTemplate");
        Assert.NotNull(dataTemplate);

        XName dataTypeAttrName = dataTemplate.Name.Namespace + "DataType";
        string? actualDataType = dataTemplate.Attribute(dataTypeAttrName)?.Value
            ?? dataTemplate.Attributes().FirstOrDefault(a => a.Name.LocalName == "DataType")?.Value;
        Assert.Equal(expectedDataType, actualDataType);

        XElement? textBlock = dataTemplate.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "TextBlock");
        Assert.NotNull(textBlock);
        Assert.Equal(expectedFontFamilyBinding, textBlock.Attribute("FontFamily")?.Value);
        Assert.Equal("{Binding}", textBlock.Attribute("Text")?.Value);
    }

    private static XElement FindAutoCompleteBoxBySelectedItem(XDocument doc, string selectedItemBinding)
    {
        XElement? box = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "AutoCompleteBox" &&
                                 e.Attribute("SelectedItem")?.Value == selectedItemBinding);
        Assert.True(box != null, $"未找到 SelectedItem 为 '{selectedItemBinding}' 的 AutoCompleteBox");
        return box!;
    }

    private static XElement FindListBoxByItemsSource(XDocument doc, string itemsSourceBinding)
    {
        XElement? listBox = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ListBox" &&
                                 e.Attribute("ItemsSource")?.Value == itemsSourceBinding);
        Assert.True(listBox != null, $"未找到 ItemsSource 为 '{itemsSourceBinding}' 的 ListBox");
        return listBox!;
    }

    private static XDocument LoadSettingsPageDocuments()
    {
        string solutionDir = TestRepository.RepositoryRoot;
        string pagesDirectory = Path.Combine(solutionDir, "src", "Kei.Term.App", "Views", "Settings");
        string[] pageFiles = Directory.GetFiles(pagesDirectory, "*PageView.axaml");
        Assert.NotEmpty(pageFiles);
        return new XDocument(new XElement("SettingsPages", pageFiles.Append(Path.Combine(pagesDirectory, "SettingsStyles.axaml"))
            .Select(file => XDocument.Load(file).Root!)));
    }

}
