using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.Templates;
using Kei.Term.Core.Models;

namespace Kei.Term.App.Views.Controls;

/// <summary>
/// 供用户选择与浏览会话树的统一共享控件，承载内建 DataTemplates 与 .sessionTree 样式作用域。
/// </summary>
public class SessionTreeView : TreeView
{
    protected override Type StyleKeyOverride => typeof(TreeView);

    public static readonly StyledProperty<IDataTemplate?> VirtualRootTemplateProperty =
        AvaloniaProperty.Register<SessionTreeView, IDataTemplate?>(nameof(VirtualRootTemplate));

    public static readonly StyledProperty<IDataTemplate?> FolderTemplateProperty =
        AvaloniaProperty.Register<SessionTreeView, IDataTemplate?>(nameof(FolderTemplate));

    public static readonly StyledProperty<IDataTemplate?> SessionTemplateProperty =
        AvaloniaProperty.Register<SessionTreeView, IDataTemplate?>(nameof(SessionTemplate));

    public IDataTemplate? VirtualRootTemplate
    {
        get => GetValue(VirtualRootTemplateProperty);
        set => SetValue(VirtualRootTemplateProperty, value);
    }

    public IDataTemplate? FolderTemplate
    {
        get => GetValue(FolderTemplateProperty);
        set => SetValue(FolderTemplateProperty, value);
    }

    public IDataTemplate? SessionTemplate
    {
        get => GetValue(SessionTemplateProperty);
        set => SetValue(SessionTemplateProperty, value);
    }

    static SessionTreeView()
    {
        VirtualRootTemplateProperty.Changed.AddClassHandler<SessionTreeView>((tree, _) => tree.RebuildTemplates());
        FolderTemplateProperty.Changed.AddClassHandler<SessionTreeView>((tree, _) => tree.RebuildTemplates());
        SessionTemplateProperty.Changed.AddClassHandler<SessionTreeView>((tree, _) => tree.RebuildTemplates());
    }

    public SessionTreeView()
    {
        Classes.Add("sessionTree");
        VirtualRootTemplate = new FuncTreeDataTemplate<VirtualRootNode>(
            (node, _) => new SessionTreeItemView(),
            node => node.Children);
        FolderTemplate = new FuncTreeDataTemplate<FolderNode>(
            (node, _) => new SessionTreeItemView(),
            node => node.Children);
        SessionTemplate = new FuncDataTemplate(
            typeof(SessionNode),
            (_, _) => new SessionTreeItemView(),
            supportsRecycling: true);
        RebuildTemplates();
    }

    private void RebuildTemplates()
    {
        DataTemplates.Clear();

        IDataTemplate? root = VirtualRootTemplate;
        IDataTemplate? folder = FolderTemplate;
        IDataTemplate? session = SessionTemplate;

        if (root != null)
        {
            DataTemplates.Add(root);
        }

        if (folder != null)
        {
            DataTemplates.Add(folder);
        }

        if (session != null)
        {
            DataTemplates.Add(session);
        }
    }
}
