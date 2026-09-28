using System.IO;
using System.Text;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Xunit;

namespace Kei.Term.Tests;

public class LocalFileTrackerTests
{
    [Fact]
    public async Task LocalFileTracker_DetectsChange_WhenFileModified()
    {
        // 1. Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "KeiTermTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var tracker = new LocalFileTracker(
                cacheBaseDirectory: tempDir,
                mode: FileWatcherMode.Polling,
                pollingIntervalSeconds: 1,
                writeDebounceMilliseconds: 200
            );

            Guid sessionId = Guid.NewGuid();
            string remotePath = "/etc/nginx/nginx.conf";
            string localFile = tracker.GetLocalCachePath(sessionId, remotePath);

            await File.WriteAllTextAsync(localFile, "server { listen 80; }");

            var tcs = new TaskCompletionSource<LocalFileChangedEventArgs>();
            tracker.FileChanged += (s, e) =>
            {
                if (e.LocalFilePath == localFile)
                {
                    tcs.TrySetResult(e);
                }
            };

            await tracker.RegisterTrackedFileAsync(sessionId, remotePath, localFile);

            // 2. Act: 模拟外部编辑器修改文件
            await Task.Delay(300);
            await File.WriteAllTextAsync(localFile, "server { listen 443 ssl; }");

            // 3. Assert
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            Assert.Same(tcs.Task, completedTask);

            var eventArgs = await tcs.Task;
            Assert.Equal(localFile, eventArgs.LocalFilePath);
            Assert.Equal(remotePath, eventArgs.RemotePath);

            await tracker.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
