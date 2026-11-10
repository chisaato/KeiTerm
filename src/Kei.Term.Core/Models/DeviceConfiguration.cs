using System.Text.Json;

namespace Kei.Term.Core.Models;

// 配置归属对象目前为会话树节点；类别和字段保持协议无关，秘密须走 Vault。
public sealed record DeviceConfigurationTarget(string? OperatingSystem, string? TargetDeviceId)
{
    public static DeviceConfigurationTarget ForOperatingSystem(string os) => new(os.ToLowerInvariant(), null);
    public static DeviceConfigurationTarget ForDevice(string deviceId) => new(null, Guid.Parse(deviceId).ToString("N"));
    public void Validate()
    {
        if ((OperatingSystem is null) == (TargetDeviceId is null)
            || OperatingSystem is not null && OperatingSystem is not ("linux" or "windows" or "macos")
            || TargetDeviceId is not null && (!Guid.TryParse(TargetDeviceId, out var id) || id.ToString("N") != TargetDeviceId))
            throw new ArgumentException("必须指定有效 OS 或目标安装实例，不能同时指定");
    }
}

public sealed record DeviceConfiguration(Guid OwnerId, string Category, DeviceConfigurationTarget Target,
    Dictionary<string, JsonElement> Fields);
