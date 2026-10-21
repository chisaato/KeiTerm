using Kei.Term.Core.Sync;

namespace Kei.Term.Infrastructure.Sync;

public sealed class PersistentDeviceId : ISyncDeviceId
{
    private readonly string _path;
    private readonly object _gate = new();
    public string? DeviceId { get; private set; }

    public PersistentDeviceId(string path)
    {
        _path = path;
        if (File.Exists(path)) DeviceId = Guid.Parse(File.ReadAllText(path)).ToString("N");
    }

    public string EnsureCreated()
    {
        lock (_gate)
        {
            if (DeviceId != null) return DeviceId;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string id = Guid.NewGuid().ToString("N");
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, id);
                try { File.Move(temp, _path); }
                catch (IOException) when (File.Exists(_path)) { id = Guid.Parse(File.ReadAllText(_path)).ToString("N"); }
                return DeviceId = id;
            }
            finally { File.Delete(temp); }
        }
    }
}
