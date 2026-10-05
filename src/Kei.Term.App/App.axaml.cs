using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Kei.Term.App.Logging;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Core.Security;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Storage.Schema;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Services;
using Microsoft.Extensions.Logging;

namespace Kei.Term.App;

public partial class App : Application
{
    // 进程级日志工厂：退出时统一 flush/释放
    private SimpleLoggerFactory? _loggerFactory;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "KeiTerm"
            );
            Directory.CreateDirectory(appDataDir);
            var dbPath = Path.Combine(appDataDir, "keiterm.db");
            var connStr = $"Data Source={dbPath}";

            // 日志目录与数据库/设置文件同源（同一 appDataDir 下的 logs/）
            var logDir = Path.Combine(appDataDir, "logs");
            _loggerFactory = new SimpleLoggerFactory(new RollingFileLoggerProvider(
                new RollingFileLoggerOptions { LogDirectory = logDir, MinimumLevel = LogLevel.Debug }));
            var logger = _loggerFactory.CreateLogger<App>();

            // 全局异常捕获：AsyncRelayCommand 之外的兜底取证
            RegisterGlobalExceptionHandlers(logger);
            RegisterShutdown(logger, desktop);

            // 桥接 Avalonia LogToTrace 的框架诊断到文件日志（绑定错误/XAML/布局等，否则被静默丢弃）
            Trace.AutoFlush = true;
            Trace.Listeners.Add(new TraceToLogBridge(_loggerFactory.CreateLogger<App>()));

            var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";
            logger.LogInformation("应用启动 版本={Version} 数据目录={DataDir} 日志目录={LogDir}", version, appDataDir, logDir);

            // 关键：全部同步构造，主窗口必须在任何 await 之前赋值。
            // lifetime 在 OnFrameworkInitializationCompleted 同步段结束后即进入 Start() 显示阶段，
            // 此时 MainWindow 若为 null，之后再赋值不会触发 Show → 进程存活但窗口永不出现。
            // 全部仓储共用一个连接工厂（统一 PRAGMA），Schema 由迁移器在下方一次性升级
            var db = new SqliteConnectionFactory(connStr);
            var treeRepo = new SqliteTreeRepository(db, _loggerFactory.CreateLogger<SqliteTreeRepository>());
            var proxyRepo = new SqliteProxyRepository(db, _loggerFactory.CreateLogger<SqliteProxyRepository>());
            var portForwards = new SqlitePortForwardRepository(db);
            var identityRepo = new SqliteIdentityRepository(db);
            var editorRepo = new SqliteExternalEditorRepository(db);
            var knownHostRepo = new SqliteKnownHostRepository(db);
            var vault = new InternalVaultManager(db, _loggerFactory.CreateLogger<InternalVaultManager>());
            var settingsPath = Path.Combine(appDataDir, "settings.json");
            var settingsService = new JsonSettingsService(settingsPath);
            // 主机密钥信任：确认框经主 VM 的交互服务弹出（窗口装配完成前按拒绝处理）
            MainViewModel? interactionHost = null;
            var hostKeyTrust = new HostKeyTrustService(
                knownHostRepo,
                () => settingsService.Current.HostKeyPolicy,
                prompt: (evaluation, _) => interactionHost == null
                    ? Task.FromResult(HostKeyDecision.Reject)
                    : interactionHost.Interaction.PromptHostKeyAsync(evaluation),
                logger: _loggerFactory.CreateLogger<HostKeyTrustService>());
            var profileManager = new ProfileManagerService(settingsService, appDataDir);
            var sshFactory = new SshSessionFactory(_loggerFactory.CreateLogger<SshSessionFactory>());

            // 先在窗口创建前按默认 KeiClassic 挂载画刷与兼容样式，避免首帧无 Kei.* 资源；
            // 读取设置后（下方）再按实际档位幂等重挂。
            UiDesignSystemService.Apply(UiDesignSystemService.KeiClassicKey);

            logger.LogInformation("启动步骤: 同步构造 ViewModel 与主窗口");
            var mainVm = new MainViewModel(
                treeRepo,
                identityRepo,
                vault,
                vault,
                settingsService,
                sshFactory,
                editorRepo,
                profileManager,
                _loggerFactory.CreateLogger<MainViewModel>(),
                _loggerFactory,
                hostKeyTrust: hostKeyTrust,
                proxyRepo: proxyRepo,
                portForwards: portForwards,
                proxySecrets: vault);
            var identityMgrVm = new IdentityManagerViewModel(
                identityRepo,
                vault,
                vault,
                _loggerFactory.CreateLogger<IdentityManagerViewModel>());
            var settingsVm = new SettingsViewModel(
                settingsService,
                appDataDir,
                identityRepo,
                profileManager,
                editorRepo,
                proxyRepo,
                () => mainVm.SnapshotSessionNodes(),
                () => mainVm.SnapshotSessionTree(),
                proxySecrets: vault);

            var mainWindow = new MainWindow
            {
                DataContext = mainVm,
            };
            mainWindow.WireDialogs(
                mainVm,
                identityMgrVm,
                settingsVm,
                _loggerFactory.CreateLogger<MainWindow>(),
                new KnownHostsManagerViewModel(knownHostRepo));
            interactionHost = mainVm;
            desktop.MainWindow = mainWindow;
            logger.LogInformation("启动完成: 主窗口已在 await 之前同步赋值");

            // 异步初始化延后到窗口赋值之后：续体经 Dispatcher 回 UI 线程，安全
            var schemaVersion = await SchemaMigrator.MigrateAsync(db);
            logger.LogInformation("数据库 Schema 版本={Version}", schemaVersion);
            // 明文模式恒解锁；加密模式等待首次用到材料时懒解锁（OS Keyring 为二期）
            await vault.TryAutoUnlockAsync();
            await settingsService.LoadSettingsAsync();
            await profileManager.InitializeAsync();

            // 按设置应用主题变体："System" 跟随系统，其余（"Dark"）固定深色
            RequestedThemeVariant = string.Equals(
                settingsService.Current.UiTheme,
                "System",
                StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Default
                : ThemeVariant.Dark;

            // 挂载 Kei 专属紧凑桌面主题与设计令牌
            UiDesignSystemService.Apply();
            UiDesignSystemService.ApplyTreeDensity(
                settingsService.Current.TreeItemHeight,
                settingsService.Current.TreeFontSize,
                settingsService.Current.TreeIconSize,
                settingsService.Current.TreeIndent);

            // 异步初始化：树加载 + 自动锁定计时器（均在 InitializeAsync 内），此时窗口已进入消息循环
            await mainVm.InitializeAsync();
            mainWindow.UpdateTabPlacement(mainVm.TabPlacement);
            logger.LogInformation("启动步骤: 异步初始化全部完成");
        }

        base.OnFrameworkInitializationCompleted();
    }

    // AppDomain / TaskScheduler / Avalonia UI 线程 未处理异常统一记为 Critical
    private static void RegisterGlobalExceptionHandlers(ILogger logger)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "AppDomain 未处理异常 IsTerminating={IsTerminating}", e.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogCritical(e.Exception, "TaskScheduler 未观察任务异常");
            // 标记已观察，避免进程被未观察异常终止
            e.SetObserved();
        };

        Dispatcher.UIThread.UnhandledException += (_, e) =>
            logger.LogCritical(e.Exception, "UI 线程未处理异常");
    }

    // 退出路径：记录并释放日志（释放内部会 flush）
    private void RegisterShutdown(ILogger logger, IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.ShutdownRequested += (_, _) => logger.LogInformation("应用收到退出请求");

        desktop.Exit += (_, _) =>
        {
            logger.LogInformation("应用正常退出");
            _loggerFactory?.Dispose();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            logger.LogInformation("进程退出");
            _loggerFactory?.Dispose();
        };
    }
}
