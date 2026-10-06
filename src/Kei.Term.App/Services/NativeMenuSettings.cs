using Avalonia;
using Kei.Term.Infrastructure.Settings;

namespace Kei.Term.App.Services;

// 在窗口创建前读取菜单偏好，设置页保存与启动读取使用同一套 JSON 设置服务。
public static class NativeMenuSettings
{
    public static X11PlatformOptions CreateX11Options(string settingsFile)
    {
        bool useNativeGlobalMenu = true;
        try
        {
            JsonSettingsService settings = new(settingsFile);

            // AppBuilder 阶段尚无 UI 同步上下文，可同步等待磁盘读取。
            settings.LoadSettingsAsync().GetAwaiter().GetResult();
            useNativeGlobalMenu = settings.Current.UseNativeGlobalMenu;
        }
        catch
        {
            // 配置不可用时仍允许桌面接管菜单，避免阻断启动。
        }

        return new X11PlatformOptions { UseDBusMenu = useNativeGlobalMenu };
    }
}
