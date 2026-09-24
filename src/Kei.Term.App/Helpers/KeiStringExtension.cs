using System;
using Avalonia.Markup.Xaml;

namespace Kei.Term.App.Helpers;

// axaml 静态取词：Text="{loc:KeiString Main.Title}"（XAML 加载时求值，v1 不做热切换）
public class KeiStringExtension : MarkupExtension
{
    public KeiStringExtension() { }
    public KeiStringExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
