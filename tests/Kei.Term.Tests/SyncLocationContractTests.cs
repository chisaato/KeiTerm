using System;
using System.IO;
using System.Threading.Tasks;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Sync;

namespace Kei.Term.Tests;

// 网络位置继承同一套行为。基类方法本身不是测试，便于 WebDAV/S3 在环境变量未开启时整组跳过。
public abstract class SyncLocationContractTests : IDisposable
{
    protected abstract ISyncLocation CreateLocation();
    public abstract void Dispose();

    protected async Task RunMissingReadAndRoundTrip()
    {
        ISyncLocation location = CreateLocation();
        await location.TestConnectionAsync();
        Assert.Null(await location.ReadAsync());
        byte[] bytes = [0, 1, 2, 254, 255];
        await location.WriteAsync(bytes);
        var result = await location.ReadAsync();
        Assert.NotNull(result);
        Assert.Equal(bytes, result.Content);
        Assert.False(string.IsNullOrEmpty(result.VersionTag));
        if (location.SupportsConditionalWrites)
        {
            await Assert.ThrowsAsync<SyncWriteConflictException>(() => location.WriteAsync([3], "stale"));
            await Assert.ThrowsAsync<SyncWriteConflictException>(() => location.WriteAsync([3]));
            await location.WriteAsync([6, 7], result.VersionTag);
            Assert.Equal(new byte[] { 6, 7 }, (await location.ReadAsync())!.Content);
        }
        else
        {
            await location.WriteAsync([3], "stale");
            Assert.Equal(new byte[] { 3 }, (await location.ReadAsync())!.Content);
        }
    }

    protected async Task RunHistoryRoundTrip()
    {
        ISyncLocation location = CreateLocation();
        Assert.Empty(await location.ListHistoryAsync());
        Assert.Null(await location.ReadHistoryAsync("missing.age"));
        await location.WriteHistoryAsync("2026-10-22.age", [4, 5, 6]);
        Assert.Equal("2026-10-22.age", Assert.Single(await location.ListHistoryAsync()).Name);
        Assert.Equal(new byte[] { 4, 5, 6 }, await location.ReadHistoryAsync("2026-10-22.age"));
        await location.DeleteHistoryAsync("2026-10-22.age");
        Assert.Empty(await location.ListHistoryAsync());
        Assert.Null(await location.ReadHistoryAsync("2026-10-22.age"));
        const string special = "历史 空格 #1?%字面量-%23.age";
        await location.WriteHistoryAsync(special, [8, 9]);
        string listedName = Assert.Single(await location.ListHistoryAsync()).Name;
        Assert.Equal(special, listedName);
        Assert.Equal(new byte[] { 8, 9 }, await location.ReadHistoryAsync(listedName));
        await location.DeleteHistoryAsync(listedName);
        await location.DeleteHistoryAsync(special);
        Assert.Empty(await location.ListHistoryAsync());
    }
}

public sealed class LocalFolderSyncLocationTests : SyncLocationContractTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keiterm-location-" + Guid.NewGuid().ToString("N"));
    protected override ISyncLocation CreateLocation() => new LocalFolderSyncLocation(_directory);

    [Fact]
    public Task MissingReadAndRoundTrip() => RunMissingReadAndRoundTrip();

    [Fact]
    public Task HistoryRoundTripListAndDelete() => RunHistoryRoundTrip();

    [Fact]
    public async Task HistoryNamesCannotEscapeDirectory()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CreateLocation().WriteHistoryAsync("../escape.age", [1]));
    }

    public override void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
