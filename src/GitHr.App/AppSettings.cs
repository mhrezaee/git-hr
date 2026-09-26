using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitHr.App;

/// <summary>Per-user settings stored in the OS app-data folder (%APPDATA%, ~/.config, ~/Library/...).</summary>
public sealed class AppSettings
{
    private const int MaxRecent = 15;

    public static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitHr", "settings.json");

    [JsonIgnore]
    public string FilePath { get; private set; } = DefaultFilePath;

    public List<string> RecentRepositories { get; set; } = [];

    /// <summary>Parent folder used for the last clone; the clone dialog starts there.</summary>
    public string? CloneFolder { get; set; }

    [JsonIgnore]
    public string EffectiveCloneFolder
    {
        get
        {
            if (!string.IsNullOrEmpty(CloneFolder) && Directory.Exists(CloneFolder))
            {
                return CloneFolder;
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var repos = Path.Combine(home, "source", "repos");
            return Directory.Exists(repos) ? repos : home;
        }
    }

    public void SetCloneFolder(string folder)
    {
        CloneFolder = folder;
        Save();
    }

    public static AppSettings Load(string? filePath = null)
    {
        filePath ??= DefaultFilePath;
        var settings = new AppSettings();
        try
        {
            if (File.Exists(filePath))
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath)) ?? settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings: start fresh.
        }
        settings.FilePath = filePath;
        return settings;
    }

    public void AddRecent(string path)
    {
        RecentRepositories.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentRepositories.Insert(0, path);
        RecentRepositories = RecentRepositories.Take(MaxRecent).ToList();
        Save();
    }

    public void RemoveRecent(string path)
    {
        RecentRepositories.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; never crash because they cannot be written.
        }
    }
}
