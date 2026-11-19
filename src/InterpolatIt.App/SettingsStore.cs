using System.IO;
using System.Text.Json;

namespace InterpolatIt.App;

/// <summary>What the user last chose; kept in %AppData%\InterpolatIt\settings.json.</summary>
public sealed class Settings
{
    public int Factor { get; set; } = 2;
    public string Model { get; set; } = "rife-v4.25";
    public string Backend { get; set; } = "";       // empty = automatic
    public bool ShowFps { get; set; }
    public bool Enhance { get; set; }               // clean up compressed video (the driver's enhancement)
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InterpolatIt", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path)) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file must never stop the app from starting: fall back to the defaults.
        }
        return new Settings();
    }

    public static void Save(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(settings, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
