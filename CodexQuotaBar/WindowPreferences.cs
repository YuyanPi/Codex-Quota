using System.IO;
using System.Text.Json;

namespace CodexQuotaBar;

internal sealed class WindowPreferences
{
    private static readonly string PreferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexQuotaBar",
        "window-state.json");

    public bool FloatingEnabled { get; set; }
    public bool IsCompact { get; set; }
    public bool Topmost { get; set; } = true;
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public bool HasPosition => double.IsFinite(Left) && double.IsFinite(Top);

    public static WindowPreferences Load()
    {
        try
        {
            if (File.Exists(PreferencesPath))
            {
                return JsonSerializer.Deserialize<WindowPreferences>(File.ReadAllText(PreferencesPath))
                       ?? new WindowPreferences();
            }
        }
        catch
        {
            // A missing or malformed preferences file should not prevent startup.
        }

        return new WindowPreferences();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
            File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Preferences are best-effort and must not interrupt quota display.
        }
    }
}

