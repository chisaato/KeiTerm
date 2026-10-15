using System;
using System.Collections.Generic;
using System.Text;

namespace Kei.Term.Core.Services;

public sealed record TabTitleContext(
    string LocalName, string? LocalHost = null, string? LocalPort = null, string? LocalUser = null,
    string? RemoteHostname = null, string? RemotePath = null, string? RemoteIp = null, string? RemoteTitle = null);

// 只替换白名单变量，不执行表达式，也不二次解析远端返回的文本。
public static class TabTitleTemplate
{
    public const int MaxLength = 512;
    public static IReadOnlyList<string> Variables { get; } = Array.AsReadOnly(new[]
    {
        "local_name", "local_host", "local_port", "local_user",
        "remote_hostname", "remote_path", "remote_dirname", "remote_ip", "remote_title"
    });

    public static bool TryRender(string? template, TabTitleContext context, out string title, out string? error)
    {
        template ??= string.Empty;
        title = string.Empty;
        error = null;
        if (template.Length > MaxLength) { error = MaxLength.ToString(); return false; }
        StringBuilder result = new();
        for (int index = 0; index < template.Length; index++)
        {
            char character = template[index];
            if (character is not ('{' or '}')) { if (!char.IsControl(character)) result.Append(character); continue; }
            if (index + 1 < template.Length && template[index + 1] == character)
            {
                result.Append(character);
                index++;
                continue;
            }
            if (character == '}') { error = "}"; return false; }
            int end = template.IndexOf('}', index + 1);
            if (end < 0) { error = "{"; return false; }
            string variable = template[(index + 1)..end];
            string? value;
            switch (variable)
            {
                case "local_name": value = context.LocalName; break;
                case "local_host": value = context.LocalHost; break;
                case "local_port": value = context.LocalPort; break;
                case "local_user": value = context.LocalUser; break;
                case "remote_hostname": value = context.RemoteHostname; break;
                case "remote_path": value = context.RemotePath; break;
                case "remote_dirname": value = DirectoryName(context.RemotePath); break;
                case "remote_ip": value = context.RemoteIp; break;
                case "remote_title": value = context.RemoteTitle; break;
                default: error = "{" + variable + "}"; return false;
            }
            if (string.IsNullOrWhiteSpace(value)) result.Append('—');
            else foreach (char part in value) if (!char.IsControl(part)) result.Append(part);
            index = end;
        }
        title = result.ToString().Trim();
        // 最长路径和远端标题重复展开时仍保持有界；完整路径保留在工具提示里。
        if (title.Length > 2048) title = title[..2048];
        return true;
    }

    public static string? DirectoryName(string? remotePath)
    {
        if (string.IsNullOrEmpty(remotePath)) return null;
        string path = remotePath.TrimEnd('/');
        return path.Length == 0 ? "/" : path[(path.LastIndexOf('/') + 1)..];
    }
}
