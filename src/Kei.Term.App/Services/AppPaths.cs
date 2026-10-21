using System;
using System.IO;
using System.Threading.Tasks;
using Kei.Term.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

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

    // Program 与独立 App 启动共用；不缓存结果，每次先查持久 journal，重复调用可安全重入。
    public static int PrepareStorage() => PrepareStorage(DataDirectory);

    internal static int PrepareStorage(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var factory = new SqliteConnectionFactory(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDirectory, "keiterm.db") }.ToString());
        var recovery = new SyncFileRecovery(factory, Path.Combine(dataDirectory, "settings.json"), Path.Combine(dataDirectory, "profiles.json"));
        // 即使由设计器/UI 上下文调用也不阻塞异步续体所需的线程。
        return Task.Run(() => recovery.PrepareStartupAsync()).GetAwaiter().GetResult();
    }
}
