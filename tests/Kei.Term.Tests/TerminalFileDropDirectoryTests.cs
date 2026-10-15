using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Infrastructure.Settings;

namespace Kei.Term.Tests;

// 覆盖 #7/#8 文件拖放上传与 #9 目录跟随合并后的共享 OSC7 事件：
// 两者现在读写同一条 TerminalDirectoryTracker 结果，拖放取到的是 tracker 接受后的
// 「最新安全合法目录」，主机不匹配或重连清空后不得残留旧路径。
public class TerminalFileDropDirectoryTests
{
    [Fact]
    public Task DropUpload_UsesLatestDirectoryFromSharedOsc7Tracker() => HeadlessAvalonia.RunAsync(async () =>
    {
        string temp = CreateDirectory();
        TerminalTabViewModel tab = CreateConfiguredTab(temp, out RecordingUploader uploader);
        try
        {
            string local = Path.Combine(temp, "payload.txt");
            await File.WriteAllTextAsync(local, "data");
            await AttachAsync(tab);

            Report(tab, "/one");
            HeadlessAvalonia.WaitUntil(() => tab.FileManager!.TerminalDirectory == "/one");
            Report(tab, "/two/deep");
            HeadlessAvalonia.WaitUntil(() => tab.FileManager!.TerminalDirectory == "/two/deep");

            await tab.HandleTerminalFileDropAsync([local]);
            // 上传目标目录与目录跟随看到的是同一个最新报告。
            Assert.Equal("/two/deep", uploader.RemoteDirectory);
        }
        finally { await tab.DisposeAsync(); Directory.Delete(temp, true); }
    });

    [Fact]
    public Task ForeignHostReport_ClearsSharedDirectory_BeforeUpload() => HeadlessAvalonia.RunAsync(async () =>
    {
        string temp = CreateDirectory();
        TerminalTabViewModel tab = CreateConfiguredTab(temp, out RecordingUploader uploader);
        try
        {
            string local = Path.Combine(temp, "payload.txt");
            await File.WriteAllTextAsync(local, "data");
            await AttachAsync(tab);

            Report(tab, "/safe");
            HeadlessAvalonia.WaitUntil(() => tab.FileManager!.TerminalDirectory == "/safe");

            // 其它主机上报：tracker 拒绝该目录并清空共享字段，上传回退到文件管理器当前目录。
            Report(tab, "/foreign", "other");
            HeadlessAvalonia.WaitUntil(() => !tab.FileManager!.HasTerminalDirectory);

            await tab.HandleTerminalFileDropAsync([local]);
            Assert.Equal(tab.FileManager!.CurrentPath, uploader.RemoteDirectory);
        }
        finally { await tab.DisposeAsync(); Directory.Delete(temp, true); }
    });

    [Fact]
    public Task Reconnect_ForgetsReportedDirectory_UntilNextSignal() => HeadlessAvalonia.RunAsync(async () =>
    {
        string temp = CreateDirectory();
        TerminalTabViewModel tab = CreateConfiguredTab(temp, out RecordingUploader uploader);
        try
        {
            string local = Path.Combine(temp, "payload.txt");
            await File.WriteAllTextAsync(local, "data");
            await AttachAsync(tab);

            Report(tab, "/before");
            HeadlessAvalonia.WaitUntil(() => tab.FileManager!.TerminalDirectory == "/before");

            // 断连清空共享目录；重连后在新信号到达前不得复用旧路径。
            await tab.DetachSessionAsync();
            tab.AttachSession(new Session());
            tab.MarkConnected();
            await tab.HandleTerminalFileDropAsync([local]);
            Assert.Equal(tab.FileManager!.CurrentPath, uploader.RemoteDirectory);

            // 新连接的新信号才重新生效。
            Report(tab, "/after");
            HeadlessAvalonia.WaitUntil(() => tab.FileManager!.TerminalDirectory == "/after");
            await tab.HandleTerminalFileDropAsync([local]);
            Assert.Equal("/after", uploader.RemoteDirectory);
        }
        finally { await tab.DisposeAsync(); Directory.Delete(temp, true); }
    });

    private static TerminalTabViewModel CreateConfiguredTab(string directory, out RecordingUploader uploader)
    {
        TerminalTabViewModel tab = new("drop-dir", "monospace", 14);
        tab.BindConfig(new ResolvedSessionConfig(Guid.NewGuid(), "drop-dir", "host", 22, "ops", null,
            "xterm-256color", null, null, new Dictionary<string, string>()));
        JsonSettingsService settings = new(Path.Combine(directory, "settings.json"));
        settings.Current.FileTransfer.TerminalDropAction = TerminalFileDropAction.Upload;
        uploader = new RecordingUploader();
        tab.ConfigureTerminalFileDrop(settings, NullInteractionService.Instance, uploader);
        return tab;
    }

    // 建立连接并初始化文件管理器，使目录跟随与上传目标都有落点。
    private static async Task AttachAsync(TerminalTabViewModel tab)
    {
        tab.AttachSession(new Session());
        tab.MarkConnected();
        await tab.InitializeFileManagerAsync(new FileSystem(), new Tracker());
        tab.IsFileManagerVisible = true;
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm-drop-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Report(TerminalTabViewModel tab, string path, string host = "host")
        => tab.Terminal.WriteOutput(Encoding.UTF8.GetBytes($"\u001b]7;file://{host}{path}\u0007"));

    private sealed class RecordingUploader : ITerminalFileUploader
    {
        public string? RemoteDirectory { get; private set; }
        public Task UploadAsync(ITerminalSession session, IReadOnlyList<string> paths, TerminalUploadProtocol protocol,
            string remoteDirectory, IProgress<TerminalUploadProgress>? progress = null, CancellationToken ct = default)
        {
            RemoteDirectory = remoteDirectory;
            return Task.CompletedTask;
        }
    }

    private sealed class Session : ITerminalSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected => true;
        public event Action<byte[]>? OutputReceived { add { } remove { } }
        public event Action<Exception?>? Disconnected { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FileSystem : IRemoteFileSystem
    {
        public bool IsConnected => true;
        public string WorkingDirectory => "/";
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RemoteFileItem>>(
                [new RemoteFileItem("file.txt", path + "/file.txt", false, 1, DateTimeOffset.UnixEpoch, "-rw-r--r--")]);
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => Task.FromResult(Stream.Null);
        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => Task.FromResult(Stream.Null);
        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default) => Task.FromResult<RemoteFileItem?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Tracker : ILocalFileTracker
    {
        public event EventHandler<LocalFileChangedEventArgs>? FileChanged { add { } remove { } }
        public event EventHandler<string>? FileUntracked { add { } remove { } }
        public string GetLocalCachePath(Guid sessionId, string remotePath) => remotePath;
        public bool IsTracking(string localFilePath) => false;
        public Task CheckForChangesAsync(string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task RegisterTrackedFileAsync(Guid sessionId, string remotePath, string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnregisterTrackedFileAsync(string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
