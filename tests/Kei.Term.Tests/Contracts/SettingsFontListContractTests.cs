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
        XDocument doc = LoadSettingsWindowDocument();

        // 1. 界面主字体 AutoCompleteBox (SelectedItem="{Binding SelectedUiFont}")
        AssertAutoCompleteBoxFontBinding(doc, "{Binding SelectedUiFont}", "services:FontFamilyOption", "{Binding FontFamily}");

        // 2. 界面回退字体候选 AutoCompleteBox (SelectedItem="{Binding UiFallbackFontCandidate}")
        AssertAutoCompleteBoxFontBinding(doc, "{Binding UiFallbackFontCandidate}", "services:FontFamilyOption", "{Binding FontFamily}");

        // 3. 界面已选回退 ListBox (ItemsSource="{Binding UiFallbackFonts}")
        AssertListBoxFontBinding(doc, "{Binding UiFallbackFonts}", "x:String", "{Binding}");

        // 4. 终端主等宽字体 AutoCompleteBox (SelectedItem="{Binding SelectedFont}")
        AssertAutoCompleteBoxFontBinding(doc, "{Binding SelectedFont}", "services:FontFamilyOption", "{Binding FontFamily}");

        // 5. 终端回退候选 AutoCompleteBox (SelectedItem="{Binding TerminalFallbackFontCandidate}")
        AssertAutoCompleteBoxFontBinding(doc, "{Binding TerminalFallbackFontCandidate}", "services:FontFamilyOption", "{Binding FontFamily}");

        // 6. 终端已选回退 ListBox (ItemsSource="{Binding TerminalFallbackFonts}")
        AssertListBoxFontBinding(doc, "{Binding TerminalFallbackFonts}", "x:String", "{Binding}");
    }

    private static void AssertAutoCompleteBoxFontBinding(
        XDocument doc,
        string selectedItemBinding,
        string expectedDataType,
        string expectedFontFamilyBinding)
    {
        XElement box = FindAutoCompleteBoxBySelectedItem(doc, selectedItemBinding);

        // 严格仅在当前 AutoCompleteBox 的直系后代元素中检索 DataTemplate，杜绝跨控件越界误判
        XElement? dataTemplate = box.Descendants()
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
        Assert.Equal("{Binding DisplayName}", textBlock.Attribute("Text")?.Value);
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

    private static XDocument LoadSettingsWindowDocument()
    {
        string solutionDir = FindSolutionRoot();
        string settingsWindowFile = Path.Combine(solutionDir, "src", "Kei.Term.App", "Views", "SettingsWindow.axaml");
        Assert.True(File.Exists(settingsWindowFile), "SettingsWindow.axaml 应存在");
        return XDocument.Load(settingsWindowFile);
    }

    private static string FindSolutionRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Kei.Term.slnx")) ||
                File.Exists(Path.Combine(current, "Kei.Term.sln")))
            {
                return current;
            }
            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
