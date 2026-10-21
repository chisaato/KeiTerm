using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kei.Term.Infrastructure.Sync;

public static class SyncLocationSettingsStore
{
    public const string FileName = "sync-location.json";
    public const string LegacyFileName = "sync-location.txt";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static SyncLocationSettings Load(string dataDirectory)
    {
        var jsonPath = Path.Combine(dataDirectory, FileName);
        if (File.Exists(jsonPath))
        {
            var settings = JsonSerializer.Deserialize<SyncLocationSettings>(File.ReadAllText(jsonPath), JsonOptions);
            return settings ?? new SyncLocationSettings();
        }

        var legacyPath = Path.Combine(dataDirectory, LegacyFileName);
        if (!File.Exists(legacyPath)) return new SyncLocationSettings();
        return new SyncLocationSettings { Kind = SyncLocationKinds.LocalFolder, Directory = File.ReadAllText(legacyPath).Trim() };
    }

    public static void Save(string dataDirectory, SyncLocationSettings settings)
    {
        Directory.CreateDirectory(dataDirectory);
        var jsonPath = Path.Combine(dataDirectory, FileName);
        var temporary = jsonPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, jsonPath, overwrite: true);
        // JSON 成为唯一来源，避免旧的纯文本目录以后又把位置改回本地文件夹。
        var legacyPath = Path.Combine(dataDirectory, LegacyFileName);
        if (File.Exists(legacyPath)) File.Delete(legacyPath);
    }
}
