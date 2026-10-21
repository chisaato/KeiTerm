namespace Kei.Term.Core.Sync;

// 本地同步实体被修改时发出一次通知。同步自身写回期间必须抑制，否则导入会再次预约同步。
public interface ISyncChangeSignal
{
    event Action? Changed;

    bool IsSuppressed { get; }

    void Publish();

    IDisposable Suppress();
}

public sealed class SyncChangeSignal : ISyncChangeSignal
{
    private static readonly AsyncLocal<int> Depth = new();

    public event Action? Changed;

    public bool IsSuppressed => Depth.Value > 0;

    public void Publish()
    {
        if (IsSuppressed) return;
        // 观察回调跑在写入语句内部，订阅者只能登记触发，不能再碰数据库。
        try { Changed?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    public IDisposable Suppress()
    {
        Depth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Depth.Value = Math.Max(0, Depth.Value - 1);
        }
    }
}
