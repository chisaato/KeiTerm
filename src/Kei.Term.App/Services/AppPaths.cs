using System;
using System.IO;

namespace Kei.Term.App.Services;

// 应用数据目录的唯一来源。
// App 初始化与 Program 启动期读设置（全局菜单开关必须早于 AppBuilder 决定）共用同一份路径逻辑，
// 避免两处各自拼接路径而漂移。
public static class AppPaths
{
    public const string FolderName = "KeiTerm";

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        FolderName);

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string DatabaseFile => Path.Combine(DataDirectory, "keiterm.db");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}
