using System.Text.Json;
using Kei.Term.Core.Models;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Sync;

namespace Kei.Term.Core.Services;

public sealed class DeviceConfigurationService(IDeviceConfigurationStore store, ISyncDeviceId device)
{
    // 编辑只读取显式字段，不把解析结果反写成覆盖；保存时移除某键即恢复下层继承。
    public Task<IReadOnlyList<DeviceConfiguration>> GetOverridesAsync(Guid ownerId, string category, CancellationToken ct = default)
    {
        device.EnsureCreated();
        return store.GetAsync(ownerId, category, ct);
    }

    public async Task<Dictionary<string, JsonElement>> ResolveAsync(Guid ownerId, string category, string os,
        IReadOnlyDictionary<string, JsonElement> defaults, CancellationToken ct = default)
    {
        string id = device.EnsureCreated();
        var result = defaults.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        var records = await store.GetAsync(ownerId, category, ct);
        foreach (var target in new[] { DeviceConfigurationTarget.ForOperatingSystem(os), DeviceConfigurationTarget.ForDevice(id) })
            foreach (var record in records.Where(r => r.Target == target))
                foreach (var field in record.Fields) result[field.Key] = field.Value.Clone();
        return result;
    }

    public Task SaveOverrideAsync(Guid ownerId, string category, DeviceConfigurationTarget target,
        Dictionary<string, JsonElement> fields, CancellationToken ct = default)
        => store.SaveAsync(new(ownerId, category, target, fields), device.EnsureCreated(), ct);

    public Task DeleteOverrideAsync(Guid ownerId, string category, DeviceConfigurationTarget target, CancellationToken ct = default)
        => store.DeleteAsync(ownerId, category, target, device.EnsureCreated(), ct);
}
