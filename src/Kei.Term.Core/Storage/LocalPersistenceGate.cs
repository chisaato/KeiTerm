namespace Kei.Term.Core.Storage;

// 设置/Profile 的原子文件替换与 SQLite 记账必须相对快照导入/导出串行。
// 只保护持久化边界，不阻塞终端活动或 UI 草稿编辑。
public static class LocalPersistenceGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<IDisposable> EnterAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Gate.Release();
        }
    }
}
