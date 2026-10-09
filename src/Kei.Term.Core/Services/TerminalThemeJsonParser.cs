using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.Core.Services;

public enum TerminalThemeJsonError
{
    EmptyInput,
    InputTooLarge,
    InvalidJson,
    ExpectedObject,
    DuplicateProperty,
    UnknownProperty,
    MissingProperty,
    UnsupportedSchemaVersion,
    InvalidName,
    InvalidColor,
    InvalidAnsiColors
}

// 位置采用编辑器可直接展示的 1-based 行/UTF-8 字节列；语义错误使用 JSON 路径定位。
public sealed record TerminalThemeJsonIssue(
    string Path,
    TerminalThemeJsonError Error,
    int? LineNumber = null,
    int? BytePositionInLine = null);

public sealed record TerminalThemeJsonParseResult(
    TerminalProfile? Profile,
    string? FormattedJson,
    IReadOnlyList<TerminalThemeJsonIssue> Issues)
{
    public bool IsValid => Profile != null && Issues.Count == 0;
}

// 公共交换格式只携带配色，不能注入内部 ID、内置标记或覆盖用户字体设置。
public static class TerminalThemeJsonParser
{
    public const int MaximumInputBytes = 256 * 1024;
    public const int MaximumNameLength = 120;

    private static readonly string[] RootProperties = ["schemaVersion", "name", "colors"];
    private static readonly string[] ColorProperties = ["background", "foreground", "cursor", "selectionBackground", "ansi"];
    private static readonly JsonSerializerOptions FormatOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ExampleJson => """
        {
          "schemaVersion": 1,
          "name": "KeiTerm 示例配色",
          "colors": {
            "background": "#1E1E1E",
            "foreground": "#D4D4D4",
            "cursor": "#D4D4D4",
            "selectionBackground": "#502472C8",
            "ansi": [
              "#000000", "#CD3131", "#0DBC79", "#E5E510",
              "#2472C8", "#BC3FBC", "#11A8CD", "#E5E5E5",
              "#666666", "#F14C4C", "#23D18B", "#F5F543",
              "#3B8EEA", "#D670D6", "#29B8DB", "#FFFFFF"
            ]
          }
        }
        """;

    public static TerminalThemeJsonParseResult Parse(string json, string? nameOverride = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (string.IsNullOrWhiteSpace(json)) return Failure(TerminalThemeJsonError.EmptyInput);
        // 先检查字符数再编码计数，避免对过大粘贴内容额外分配内存。
        if (json.Length > MaximumInputBytes || Encoding.UTF8.GetByteCount(json) > MaximumInputBytes)
            return Failure(TerminalThemeJsonError.InputTooLarge);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException exception)
        {
            return new(null, null, [new TerminalThemeJsonIssue(
                "$", TerminalThemeJsonError.InvalidJson,
                (int?)exception.LineNumber + 1, (int?)exception.BytePositionInLine + 1)]);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            // 格式化原始 JSON 而非领域模型，语义校验失败也不会丢掉未知字段或用户输入。
            string formattedJson = JsonSerializer.Serialize(root, FormatOptions);
            List<TerminalThemeJsonIssue> issues = [];
            if (root.ValueKind != JsonValueKind.Object)
                return new(null, formattedJson, [new("$", TerminalThemeJsonError.ExpectedObject)]);

            CheckProperties(root, "$", RootProperties, issues);
            if (!root.TryGetProperty("schemaVersion", out JsonElement version))
                issues.Add(new("$.schemaVersion", TerminalThemeJsonError.MissingProperty));
            else if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1)
                issues.Add(new("$.schemaVersion", TerminalThemeJsonError.UnsupportedSchemaVersion));

            string? jsonName = null;
            if (root.TryGetProperty("name", out JsonElement name))
            {
                if (name.ValueKind == JsonValueKind.String) jsonName = name.GetString();
                else issues.Add(new("$.name", TerminalThemeJsonError.InvalidName));
                if (name.ValueKind == JsonValueKind.String && !IsName(jsonName))
                    issues.Add(new("$.name", TerminalThemeJsonError.InvalidName));
            }
            string effectiveName = (!string.IsNullOrWhiteSpace(nameOverride) ? nameOverride : jsonName)?.Trim() ?? "";
            if ((!IsName(effectiveName) || nameOverride?.Any(char.IsControl) == true)
                && !issues.Any(issue => issue.Path == "$.name"))
                issues.Add(new("$.name", TerminalThemeJsonError.InvalidName));

            if (!root.TryGetProperty("colors", out JsonElement colors))
            {
                issues.Add(new("$.colors", TerminalThemeJsonError.MissingProperty));
                return new(null, formattedJson, issues.AsReadOnly());
            }
            if (colors.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("$.colors", TerminalThemeJsonError.ExpectedObject));
                return new(null, formattedJson, issues.AsReadOnly());
            }

            CheckProperties(colors, "$.colors", ColorProperties, issues);
            string? background = ReadColor(colors, "background", required: true, allowAlpha: true, issues);
            string? foreground = ReadColor(colors, "foreground", required: true, allowAlpha: true, issues);
            string? cursor = ReadColor(colors, "cursor", required: false, allowAlpha: true, issues);
            string? selection = ReadColor(colors, "selectionBackground", required: false, allowAlpha: true, issues);
            string[]? ansi = ReadAnsiColors(colors, issues);

            if (issues.Count > 0) return new(null, formattedJson, issues.AsReadOnly());

            // 必填字段已验证；缺失的可选色遵循 Konsole 导入器相同的确定性推导。
            TerminalProfile profile = new()
            {
                Name = effectiveName,
                Background = background!,
                Foreground = foreground!,
                CursorColor = cursor ?? foreground!,
                SelectionBackground = selection ?? "#50" + ansi![4][^6..],
                AnsiColors = ansi!,
                IsBuiltIn = false
            };
            return new(profile, formattedJson, issues.AsReadOnly());
        }
    }

    // 从通用编辑器导出当前配色，保留透明度；内部 ID 与字体不进入交换格式。
    public static string Export(TerminalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            name = IsName(profile.Name) ? profile.Name.Trim() : "自定义终端主题",
            colors = new
            {
                background = ExpandColor(profile.Background),
                foreground = ExpandColor(profile.Foreground),
                cursor = ExpandColor(profile.CursorColor),
                selectionBackground = ExpandColor(profile.SelectionBackground),
                ansi = profile.AnsiColors.Select(ExpandColor).ToArray()
            }
        }, FormatOptions);
    }

    private static string ExpandColor(string color)
        => color.Length == 4 && color[0] == '#'
            ? $"#{color[1]}{color[1]}{color[2]}{color[2]}{color[3]}{color[3]}".ToUpperInvariant()
            : color.ToUpperInvariant();

    public static string CreateLlmPrompt(string? name = null)
    {
        // 通过 JSON 序列化插入名称，引号/反斜杠等不能破坏给 LLM 的示例结构。
        using JsonDocument example = JsonDocument.Parse(ExampleJson);
        string requestedName = IsName(name) ? name!.Trim() : "导入的配色";
        string namedExample = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            name = requestedName,
            colors = example.RootElement.GetProperty("colors")
        }, FormatOptions);

        return $$"""
            请把下方原始配色文件转换为 KeiTerm 通用终端配色 JSON v1。
            只输出一个完整 JSON 对象，不要 Markdown 代码围栏、注释或说明文字。

            规则：
            - 顶层只允许 schemaVersion、name、colors；schemaVersion 必须为整数 1。
            - name 使用示例中的名称，1–120 个字符，不含控制字符。
            - colors 必须包含 background、foreground、ansi；可选 cursor、selectionBackground，不允许其他字段。
            - 所有色值使用 #RRGGBB；原配色有透明度时使用 #AARRGGBB。八位颜色的前两位是透明度 AA，不能输出 #RRGGBBAA。
            - ansi 恰好 16 项：黑、红、绿、黄、蓝、品红、青、白，然后按相同顺序排列 8 个亮色。
            - 优先保留原文件的对应色。缺失 cursor 时使用 foreground；缺失 selectionBackground 时使用 #50 加 ANSI 蓝色的六位 RGB。
            - 缺失 ANSI 槽位时，依据原配色风格补齐，保持每个槽位的色相语义；不要把语法高亮色直接按顺序当成 ANSI 色。
            - 将 RGB 数值、Konsole 的逗号分隔 RGB、iTerm2 的 0–1 分量或其他颜色格式换算为六位十六进制；分量换算到 0–255 后四舍五入。
            - 不包含字体、字号、文件路径、内部 ID 或 IsBuiltIn；示例中的颜色只展示格式，应替换为原文件的配色。

            输出结构示例：
            {{namedExample}}

            原始配色文件：
            [在此粘贴原始配色文件内容]
            """;
    }

    private static TerminalThemeJsonParseResult Failure(TerminalThemeJsonError error)
        => new(null, null, [new("$", error)]);

    private static void CheckProperties(JsonElement element, string path, string[] allowed, List<TerminalThemeJsonIssue> issues)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string propertyPath = path + "." + property.Name;
            if (!seen.Add(property.Name)) issues.Add(new(propertyPath, TerminalThemeJsonError.DuplicateProperty));
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                issues.Add(new(propertyPath, TerminalThemeJsonError.UnknownProperty));
        }
    }

    private static string? ReadColor(JsonElement colors, string key, bool required, bool allowAlpha, List<TerminalThemeJsonIssue> issues)
    {
        string path = "$.colors." + key;
        if (!colors.TryGetProperty(key, out JsonElement color))
        {
            if (required) issues.Add(new(path, TerminalThemeJsonError.MissingProperty));
            return null;
        }
        string? value = color.ValueKind == JsonValueKind.String ? color.GetString() : null;
        if (!IsColor(value, allowAlpha))
        {
            issues.Add(new(path, TerminalThemeJsonError.InvalidColor));
            return null;
        }
        return value!.ToUpperInvariant();
    }

    private static string[]? ReadAnsiColors(JsonElement colors, List<TerminalThemeJsonIssue> issues)
    {
        const string path = "$.colors.ansi";
        if (!colors.TryGetProperty("ansi", out JsonElement ansi))
        {
            issues.Add(new(path, TerminalThemeJsonError.MissingProperty));
            return null;
        }
        if (ansi.ValueKind != JsonValueKind.Array || ansi.GetArrayLength() != 16)
        {
            issues.Add(new(path, TerminalThemeJsonError.InvalidAnsiColors));
            return null;
        }
        string[] values = new string[16];
        for (int index = 0; index < values.Length; index++)
        {
            JsonElement color = ansi[index];
            string? value = color.ValueKind == JsonValueKind.String ? color.GetString() : null;
            if (!IsColor(value, allowAlpha: true)) issues.Add(new($"{path}[{index}]", TerminalThemeJsonError.InvalidColor));
            else values[index] = value!.ToUpperInvariant();
        }
        return values;
    }

    private static bool IsColor(string? value, bool allowAlpha)
        => value != null && (value.Length == 7 || (allowAlpha && value.Length == 9))
            && value[0] == '#' && value.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF".AsSpan()) == false;

    private static bool IsName(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumNameLength && !value.Any(char.IsControl);
}
