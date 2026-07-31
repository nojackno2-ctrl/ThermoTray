using System.Text.Json;
using System.IO;

namespace ThermoTray;

public sealed class SettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ThermoTray",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable or corrupt preferences file must not stop temperature monitoring.
            return new AppSettings();
        }
    }

    /// <summary>
    /// Writes through a temporary file so an interrupted write cannot leave a half-written
    /// preferences file behind. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when the profile is locked or read-only.
    /// </summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporaryPath = FilePath + ".tmp";

        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings));
            File.Move(temporaryPath, FilePath, true);
        }
        catch
        {
            Delete(temporaryPath);
            throw;
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The leftover temporary file is harmless and the next save overwrites it.
        }
    }
}

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }

    public bool HideWhenClosed { get; set; } = true;

    public string Language { get; set; } = Localizer.DefaultLanguage;
}
