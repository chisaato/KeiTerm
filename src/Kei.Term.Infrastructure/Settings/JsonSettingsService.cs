using System.Text.Json;

namespace Kei.Term.Infrastructure.Settings;

using Kei.Term.Core.Settings;

public class JsonSettingsService : ISettingsService, INotifySettingsCommitted
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    // 串行化并发保存，避免两次写入交错
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private AppSettings _current;
    // 字号提交代际。旧的落盘任务发现代际已前进时，不得把捕获的旧字号写回内存或磁盘。
    private int _fontGeneration;
    private long _latestFontBits;

    public AppSettings Current => _current;

    private double LatestFontSize => BitConverter.Int64BitsToDouble(Volatile.Read(ref _latestFontBits));

    // 内存中的已提交设置已变化。在调用线程、落盘之前触发，便于设置页立刻看到新字号。
    public event EventHandler? SettingsCommitted;

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

    // 字号单独提交：调用线程立刻看到最后一次字号并通知。
    // 落盘任务只认代际；旧任务不得把捕获值写回，避免 15 排在 16 后面时把内存打回 15。
    public Task CommitFontSizeAsync(double fontSize, CancellationToken ct = default)
    {
        int generation = PublishFontSize(fontSize);
        SettingsCommitted?.Invoke(this, EventArgs.Empty);
        return PersistFontSizeAsync(generation, ct);
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        int seenGeneration = Volatile.Read(ref _fontGeneration);
        await _saveLock.WaitAsync(ct);
        try
        {
            _current = settings;
            // 等待锁期间又有字号提交：保留那次字号，只采用本次快照的其他字段
            if (Volatile.Read(ref _fontGeneration) != seenGeneration)
            {
                _current.FontSize = LatestFontSize;
            }

            await WriteCurrentAsync(ct);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private int PublishFontSize(double fontSize)
    {
        Volatile.Write(ref _latestFontBits, BitConverter.DoubleToInt64Bits(fontSize));
        _current.FontSize = fontSize;
        return Interlocked.Increment(ref _fontGeneration);
    }

    private async Task PersistFontSizeAsync(int generation, CancellationToken ct)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            // 仍是最后一次提交时，才把最新字号补到当前对象上（整对象保存替换过 Current 也不回滚其他字段）
            if (generation == Volatile.Read(ref _fontGeneration))
            {
                _current.FontSize = LatestFontSize;
            }

            await OnSaveLockHeldAsync(ct);

            // 钩子等待期间可能又有更新的提交。旧任务到此结束，不写内存、不落盘。
            if (generation != Volatile.Read(ref _fontGeneration))
            {
                return;
            }

            _current.FontSize = LatestFontSize;
            await WriteCurrentAsync(ct);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    // 保存锁已持有、写盘之前。生产路径为空；测试重写它来确定性插队，不要在生产里等待。
    protected virtual Task OnSaveLockHeldAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task WriteCurrentAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 原子写入：先写同目录临时文件再整体替换，进程崩溃/断电不会留下半截 JSON
        var tempPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(_current, WriteOptions);
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, _filePath, overwrite: true);
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
