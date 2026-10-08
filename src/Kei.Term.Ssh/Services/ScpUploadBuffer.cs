namespace Kei.Term.Ssh.Services;

// 释放只回收内存。只有完整读取源文件并显式提交后才允许发送，取消/异常展开不能上传半份文件。
internal sealed class ScpUploadBuffer(Func<Stream, CancellationToken, Task> upload) : MemoryStream
{
    private bool _committed;

    public async Task CommitAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(!CanRead, this);
        ct.ThrowIfCancellationRequested();
        if (_committed)
        {
            return;
        }

        Position = 0;
        await upload(this, ct);
        _committed = true;
    }
}
