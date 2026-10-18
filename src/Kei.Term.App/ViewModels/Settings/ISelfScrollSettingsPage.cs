namespace Kei.Term.App.ViewModels.Settings;

// 标记设置页面自身接管滚动容器（例如具有固定预览栏的响应式双栏页面），
// SettingsWindow 据此对当前页面关闭外层 ScrollViewer。
public interface ISelfScrollSettingsPage
{
    // 是否正在接管滚动。若页面进入下钻子页，通常应退回整页滚动。
    bool ManagesOwnScroll { get; }
}
