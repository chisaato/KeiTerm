namespace Kei.Term.Core.Storage;

// 文件内容尚未追上已提交的同步版本时，禁止加载基线或继续保存旧内存状态。
public interface ILocalPersistenceReadiness
{
    Task EnsureReadyAsync(CancellationToken ct = default);
}
