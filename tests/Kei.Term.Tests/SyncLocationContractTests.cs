using System;
using System.IO;
using System.Threading.Tasks;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Sync;
using Xunit;

namespace Kei.Term.Tests;

// 06/07 可继承同一契约并只替换 CreateLocation；网络位置的 opt-in 夹具由各工单提供。
public abstract class SyncLocationContractTests : IDisposable
{
    protected abstract ISyncLocation CreateLocation();
    public abstract void Dispose();

    [Fact]
    public async Task MissingReadAndRoundTrip()
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
            await Assert.ThrowsAsync<SyncWriteConflictException>(() => location.WriteAsync([3], "stale"));
        else
        {
            await location.WriteAsync([3], "stale");
            Assert.Equal(new byte[] { 3 }, (await location.ReadAsync())!.Content);
        }
    }

    [Fact]
    public async Task HistoryRoundTripListAndDelete()
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
    }
}

public sealed class LocalFolderSyncLocationTests : SyncLocationContractTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keiterm-location-" + Guid.NewGuid().ToString("N"));
    protected override ISyncLocation CreateLocation() => new LocalFolderSyncLocation(_directory);

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
