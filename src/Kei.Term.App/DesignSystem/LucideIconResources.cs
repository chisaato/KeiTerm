using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Kei.Term.App.DesignSystem;

public sealed class LucideIconResources : ResourceDictionary
{
    public LucideIconResources()
    {
        ResourceDictionary source = (ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://Kei.Term.App/DesignSystem/KeiIcons.axaml"), null)!;
        foreach (object key in source.Keys.ToArray())
        {
            // 编译后的 XAML 资源延迟创建，通过资源接口解析实际 Geometry。
            source.TryGetResource(key, null, out object? value);
            // 细长与分散轮廓在 16px 工具栏中偏轻，按实际渲染结果少量补偿。
            double opticalScale = key switch
            {
                "Kei.Icon.Connect" => 1.06,
                "Kei.Icon.QuickConnect" => 1.04,
                "Kei.Icon.Disconnect" => 1.03,
                _ => 1
            };
            Add(key, LucideIconGeometry.Normalize((Geometry)value!, opticalScale));
        }
    }
}
