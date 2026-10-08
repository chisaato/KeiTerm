namespace Kei.Term.App.Workspaces;

using System;
using System.ComponentModel;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Kei.Term.App.ViewModels;

// 一次打开对应一个文档。Id 是本次实例，不能用会话配置 Id。
public sealed class TerminalWorkspaceDocument : WorkspaceDocument
{
    public TerminalWorkspaceDocument(TerminalTabViewModel tab) : base(tab, tab.Title)
    {
        ArgumentNullException.ThrowIfNull(tab);
        Tab = tab;
        // 不写 DockCapabilityOverrides。个体优先级最高，写成 allow 会盖过 root 禁浮动。
        tab.PropertyChanged += OnTabPropertyChanged;
    }

    public TerminalTabViewModel Tab { get; }

    // 只解除标题订阅。连接释放仍走现有关闭链路。
    public override void Detach()
    {
        Tab.PropertyChanged -= OnTabPropertyChanged;
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalTabViewModel.Title))
        {
            Title = Tab.Title;
        }
    }
}
