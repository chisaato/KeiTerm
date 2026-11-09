using Kei.Term.Core.Models;

namespace Kei.Term.Core.Storage;

public interface IDeviceConfigurationStore
{
    Task<IReadOnlyList<DeviceConfiguration>> GetAsync(Guid ownerId, string category, CancellationToken ct = default);
    Task SaveAsync(DeviceConfiguration configuration, string sourceDeviceId, CancellationToken ct = default);
    Task DeleteAsync(Guid ownerId, string category, DeviceConfigurationTarget target, string sourceDeviceId, CancellationToken ct = default);
}
