using System.Text.Json;

namespace Kei.Term.Infrastructure.Settings;

using Kei.Term.Core.Settings;

public class JsonSettingsService : ISettingsService
{
    private readonly string _filePath;
    private AppSettings _current;

    public AppSettings Current => _current;

    public JsonSettingsService(string filePath)
    {
        _filePath = filePath;
        _current = new AppSettings();
    }

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
        {
            _current = new AppSettings();
            return _current;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json);
            _current = loaded ?? new AppSettings();
        }
        catch
        {
            _current = new AppSettings();
        }

        return _current;
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        _current = settings;
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_filePath, json, ct);
    }
}
