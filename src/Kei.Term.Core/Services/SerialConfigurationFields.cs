using System.Text.Json;
using Kei.Term.Core.Models;

namespace Kei.Term.Core.Services;

// 工厂与编辑界面共用的设备配置边界；超时属于运行策略，不参与设备配置同步。
public static class SerialConfigurationFields
{
    public static Dictionary<string, JsonElement> FromOptions(SerialOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // 下层配置可尚缺端口，完整运行参数只在覆盖合并后由 Apply 校验。
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["portName"] = JsonSerializer.SerializeToElement(options.PortName),
            ["baudRate"] = JsonSerializer.SerializeToElement(options.BaudRate),
            ["dataBits"] = JsonSerializer.SerializeToElement(options.DataBits),
            ["parity"] = JsonSerializer.SerializeToElement(options.Parity.ToString()),
            ["stopBits"] = JsonSerializer.SerializeToElement(options.StopBits.ToString()),
            ["flowControl"] = JsonSerializer.SerializeToElement(options.FlowControl.ToString())
        };
        if (options.DtrEnable is { } dtr) fields["dtrEnable"] = JsonSerializer.SerializeToElement(dtr);
        if (options.RtsEnable is { } rts) fields["rtsEnable"] = JsonSerializer.SerializeToElement(rts);
        return fields;
    }

    public static SerialOptions Apply(SerialOptions defaults, IReadOnlyDictionary<string, JsonElement> fields)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(fields);
        var options = defaults;
        foreach (var (name, value) in fields)
        {
            options = name switch
            {
                "portName" => options with { PortName = ReadString(name, value) },
                "baudRate" => options with { BaudRate = ReadInt32(name, value) },
                "dataBits" => options with { DataBits = ReadInt32(name, value) },
                "parity" => options with { Parity = ReadEnum<SerialParity>(name, value) },
                "stopBits" => options with { StopBits = ReadEnum<SerialStopBits>(name, value) },
                "flowControl" => options with { FlowControl = ReadEnum<SerialFlowControl>(name, value) },
                "dtrEnable" => options with { DtrEnable = ReadBoolean(name, value) },
                "rtsEnable" => options with { RtsEnable = ReadBoolean(name, value) },
                // 未知字段可由上层保留，但不能穿过串口参数白名单改变运行策略。
                _ => options
            };
        }
        options.Validate();
        return options;
    }

    private static string ReadString(string name, JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw InvalidField(name);

    private static int ReadInt32(string name, JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : throw InvalidField(name);

    private static bool ReadBoolean(string name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw InvalidField(name)
    };

    private static T ReadEnum<T>(string name, JsonElement value) where T : struct, Enum
    {
        var text = ReadString(name, value);
        if (Enum.TryParse<T>(text, false, out var result) && Enum.IsDefined(result) && result.ToString() == text)
            return result;
        throw InvalidField(name);
    }

    private static ArgumentException InvalidField(string name) =>
        new($"串口配置字段 {name} 的类型或取值无效。", nameof(name));
}
