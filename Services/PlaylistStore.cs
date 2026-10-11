using System.IO;
using System.Text.Json;

namespace MediaPlayer.Services;

public static class PlaylistStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MediaPlayer");

    private static string PlaylistFile => Path.Combine(Folder, "playlist.json");

    private static string SettingsFile => Path.Combine(Folder, "settings.json");

    public static List<string> LoadPlaylist()
    {
        try
        {
            if (!File.Exists(PlaylistFile))
            {
                return new List<string>();
            }

            var json = File.ReadAllText(PlaylistFile);
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static void SavePlaylist(IEnumerable<string> paths)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PlaylistFile, JsonSerializer.Serialize(paths.ToList(), WriteOptions));
        }
        catch
        {
        }
    }

    public static double LoadVolume()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return 0.8d;
            }

            var json = File.ReadAllText(SettingsFile);
            var settings = JsonSerializer.Deserialize<SettingsData>(json);
            return settings is null ? 0.8d : Math.Clamp(settings.Volume, 0d, 1d);
        }
        catch
        {
            return 0.8d;
        }
    }

    public static void SaveVolume(double volume)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var settings = new SettingsData { Volume = Math.Clamp(volume, 0d, 1d) };
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, WriteOptions));
        }
        catch
        {
        }
    }

    private sealed class SettingsData
    {
        public double Volume { get; set; } = 0.8d;
    }
}
