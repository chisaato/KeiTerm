using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Infrastructure.Vault.Linux;
using Kei.Term.Infrastructure.Vault.MacOS;
using Kei.Term.Infrastructure.Vault.Windows;

namespace Kei.Term.App;

public partial class App
{
    private static IDeviceQuickUnlockStore CreateQuickUnlockStore()
    {
        if (System.OperatingSystem.IsMacOS()) return new MacOsDeviceQuickUnlockStore();
        if (System.OperatingSystem.IsWindows()) return new WindowsDeviceQuickUnlockStore(QuickUnlockWindowHandleAsync);
        if (System.OperatingSystem.IsLinux()) return new LinuxSecretServiceQuickUnlockStore();
        return new UnavailableQuickUnlockStore();
    }

    private static async Task<nint> QuickUnlockWindowHandleAsync()
        => await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // 原生认证跟随当前模态窗口；不从后台线程访问 Avalonia 控件。
            Window? owner = CurrentMainWindow;
            while (owner?.OwnedWindows.LastOrDefault(window => window.IsVisible) is { } child) owner = child;
            return owner?.TryGetPlatformHandle()?.Handle ?? 0;
        });
}
