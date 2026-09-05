using System.IO;
using System.Text.Json;

namespace CodexQuotaBar;

public static class QuotaMapper
{
    public static QuotaSnapshot Map(JsonElement rateLimitResult, string? configuredModel)
    {
        var snapshot = SelectSnapshot(rateLimitResult);
        var windows = new List<QuotaWindow>();

        AddWindow(snapshot, "primary", windows);
        AddWindow(snapshot, "secondary", windows);

        var fiveHour = windows.FirstOrDefault(w => w.WindowDurationMinutes is >= 240 and <= 360);
        var weekly = windows.FirstOrDefault(w => w.WindowDurationMinutes is >= 9_000 and <= 11_000);

        // Older app-server responses may omit durations. The historical wire shape
        // uses primary for the short window and secondary for the long window.
        fiveHour ??= windows.ElementAtOrDefault(0);
        weekly ??= windows.FirstOrDefault(w => !ReferenceEquals(w, fiveHour))
                  ?? windows.ElementAtOrDefault(1);

        var displayName = string.IsNullOrWhiteSpace(configuredModel)
            ? ReadString(snapshot, "limitName") ?? "Codex"
            : configuredModel!;

        return new QuotaSnapshot(
            displayName,
            fiveHour ?? UnknownWindow(),
            weekly ?? UnknownWindow(),
            DateTimeOffset.Now,
            "Codex 本地登录态");
    }

    private static JsonElement SelectSnapshot(JsonElement result)
    {
        if (result.TryGetProperty("rateLimitsByLimitId", out var byId) &&
            byId.ValueKind == JsonValueKind.Object)
        {
            if (byId.TryGetProperty("codex", out var codex))
            {
                return codex;
            }

            foreach (var item in byId.EnumerateObject())
            {
                if (HasQuotaWindow(item.Value))
                {
                    return item.Value;
                }
            }
        }

        if (result.TryGetProperty("rateLimits", out var legacy))
        {
            return legacy;
        }

        throw new InvalidDataException("额度响应中没有 rateLimits 数据。");
    }

    private static bool HasQuotaWindow(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object &&
        (value.TryGetProperty("primary", out _) || value.TryGetProperty("secondary", out _));

    private static void AddWindow(JsonElement snapshot, string propertyName, ICollection<QuotaWindow> windows)
    {
        if (!snapshot.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        int? used = ReadInt(value, "usedPercent");
        int? remaining = used is null ? null : Math.Clamp(100 - used.Value, 0, 100);
        int? duration = ReadInt(value, "windowDurationMins");
        long? resetSeconds = ReadLong(value, "resetsAt");
        DateTimeOffset? reset = resetSeconds is null
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(resetSeconds.Value);

        windows.Add(new QuotaWindow(remaining, duration, reset));
    }

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static long? ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static QuotaWindow UnknownWindow() => new(null, null, null);
}
