using System.Text.Json;

namespace RigScope.Core.Settings;

/// <summary>The user's choices in Settings. Missing or unreadable files fall back to the defaults.</summary>
public sealed record UserSettings
{
    /// <summary>Ask GitHub for the latest release when RigScope starts. Nothing is downloaded without the user.</summary>
    public bool CheckForUpdatesOnStartup { get; init; } = true;
}

/// <summary>Stores <see cref="UserSettings"/> as JSON in %LOCALAPPDATA%\RigScope\settings.json.</summary>
public sealed class SettingsStore(string? path = null)
{
    public string Path { get; } = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RigScope", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public UserSettings Load()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Path), Json) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>Saves the settings. Returns false if the file couldn't be written (the choice still applies this session).</summary>
    public bool Save(UserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(settings, Json));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
