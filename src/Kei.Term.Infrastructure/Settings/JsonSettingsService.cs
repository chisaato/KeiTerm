using System.Text.Json;

namespace Kei.Term.Infrastructure.Settings;

using Kei.Term.Core.Settings;

public class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    // 串行化并发保存，避免两次写入交错
    private readonly SemaphoreSlim _saveLock = new(1, 1);
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
        catch (JsonException)
        {
            // 损坏的设置文件先备份再回退默认值：下次保存会覆盖原文件，不备份即永久丢失用户配置
            BackupCorruptFile();
            _current = new AppSettings();
        }
        catch (IOException)
        {
            _current = new AppSettings();
        }

        return _current;
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            _current = settings;
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // 原子写入：先写同目录临时文件再整体替换，进程崩溃/断电不会留下半截 JSON
            var tempPath = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(settings, WriteOptions);
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private void BackupCorruptFile()
    {
        try
        {
            var backupPath = $"{_filePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_filePath, backupPath, overwrite: true);
        }
        catch (IOException)
        {
            // 备份失败不阻断启动
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }
}
