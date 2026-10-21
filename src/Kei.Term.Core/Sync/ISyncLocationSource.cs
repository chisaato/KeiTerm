namespace Kei.Term.Core.Sync;

// 当前位置由应用装配决定。HasConfiguration 不读取保险库，打开位置可以是异步的。
public interface ISyncLocationSource
{
    bool HasConfiguration { get; }

    Task<ISyncLocation?> OpenAsync(CancellationToken cancellationToken = default);
}
