namespace Kei.Term.Core.Models;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// 外部编辑器实体定义
public class ExternalEditor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string ArgumentsTemplate { get; set; } = "\"{path}\"";
    public bool IsDefault { get; set; }
    public List<ExternalEditorPath> Paths { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // 解析当前操作系统的有效路径：优先当前系统专属路径，没有则回退到 "any" 通用命令
    public string? GetEffectivePath(string currentOs)
    {
        var rawPath = Paths.FirstOrDefault(p => string.Equals(p.Os, currentOs, StringComparison.OrdinalIgnoreCase))?.Path
                   ?? Paths.FirstOrDefault(p => string.Equals(p.Os, "any", StringComparison.OrdinalIgnoreCase))?.Path;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        // 展开环境变量（如 %LOCALAPPDATA% 或 $HOME / ~）
        string expanded = Environment.ExpandEnvironmentVariables(rawPath);
        if (expanded.StartsWith("~") && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = Path.Combine(home, expanded.Length > 1 ? expanded.Substring(2) : string.Empty);
        }

        return expanded;
    }
}

// 平台专属或通用路径
public record ExternalEditorPath(Guid Id, Guid EditorId, string Os, string Path);

// 文件类型/后缀与外部编辑器的关联规则
public class FileAssociationRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // 匹配模式，支持分号分隔的通配符，如 "*.log;*.txt"、"Dockerfile"、"*.json"
    public string Pattern { get; set; } = string.Empty;

    // 绑定的外部编辑器 ID
    public Guid EditorId { get; set; }

    // 匹配优先级，数字越大优先级越高
    public int Priority { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// 跨平台操作系统标识助手
public static class PlatformHelper
{
    public static string CurrentOs => true switch
    {
        _ when OperatingSystem.IsWindows() => "windows",
        _ when OperatingSystem.IsLinux() => "linux",
        _ when OperatingSystem.IsMacOS() => "macos",
        _ when OperatingSystem.IsAndroid() => "android",
        _ => "unknown"
    };
}
