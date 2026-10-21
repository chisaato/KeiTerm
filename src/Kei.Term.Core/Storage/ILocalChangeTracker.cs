namespace Kei.Term.Core.Storage;

// 首次同步前允许为空；03 可注入持久化的本机设备标识，不涉及授权。
public interface ICurrentDeviceId
{
    string? DeviceId { get; }
}

public interface ILocalChangeTracker
{
    Task TrackAsync(string entityType, string entityId, bool deleted = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalEntityStamp>> GetAllAsync(CancellationToken cancellationToken = default);
    Task PurgeTombstonesBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);
}

public sealed record LocalEntityStamp(string EntityType, string EntityId, DateTime UpdatedAtUtc, string? DeviceId, DateTime? DeletedAtUtc, long Revision);
