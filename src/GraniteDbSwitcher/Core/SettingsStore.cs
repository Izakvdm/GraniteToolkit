using System.Text.Json;
using GraniteDbSwitcher.Models;

namespace GraniteDbSwitcher.Core;

/// <summary>Remembered between runs. Never holds a password.</summary>
public sealed class SwitcherSettings
{
    public SqlAuthMode SqlAuth { get; set; } = SqlAuthMode.Windows;
    public string? SqlUser { get; set; }
    public string? LastInstall { get; set; }
    public bool ShowAllDatabases { get; set; }
    public bool FixLoginAccess { get; set; } = true;
}

public static class SettingsStore
{
    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Granite DB Switcher");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static SwitcherSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SwitcherSettings>(File.ReadAllText(FilePath)) ?? new SwitcherSettings();
        }
        catch { /* unreadable settings: start fresh */ }
        return new SwitcherSettings();
    }

    public static void Save(SwitcherSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}

/// <summary>Appends every log line to ProgramData\Granite DB Switcher\Logs\switch-yyyyMMdd.log.</summary>
public static class LogFile
{
    public static string Folder => Path.Combine(SettingsStore.Folder, "Logs");

    public static void Append(LogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string file = Path.Combine(Folder, $"switch-{entry.Timestamp:yyyyMMdd}.log");
            File.AppendAllText(file, $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss} [{entry.Level}] {entry.Message}{Environment.NewLine}");
        }
        catch { /* logging must never break a switch */ }
    }
}
