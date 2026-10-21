namespace Kei.Term.Core.Storage;

using System.Text.Json;

// 按设置叶子项记账，数组作为一个有序值；不把整组设置当作一个实体。
public static class LocalChangeTracking
{
    public static Dictionary<string, string> Flatten(JsonElement value, string prefix = "")
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        Visit(value, prefix, result);
        return result;
    }

    private static void Visit(JsonElement value, string prefix, Dictionary<string, string> result)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
                Visit(property.Value, prefix.Length == 0 ? property.Name : prefix + "." + property.Name, result);
        }
        else result[prefix] = value.GetRawText();
    }

    public static async Task TrackChangesAsync(ILocalChangeTracker tracker, string type,
        IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after, CancellationToken ct = default)
    {
        var known = (await tracker.GetAllAsync(ct)).Where(s => s.EntityType == type).ToDictionary(s => s.EntityId, StringComparer.Ordinal);
        foreach (var pair in after)
            if (!before.TryGetValue(pair.Key, out string? old) || old != pair.Value || !known.ContainsKey(pair.Key))
                await tracker.TrackAsync(type, pair.Key, false, ct);
        foreach (var key in before.Keys.Except(after.Keys, StringComparer.Ordinal))
            await tracker.TrackAsync(type, key, true, ct);
    }
}
