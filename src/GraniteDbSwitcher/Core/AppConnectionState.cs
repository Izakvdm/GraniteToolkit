using GraniteDbSwitcher.Models;

namespace GraniteDbSwitcher.Core;

/// <summary>What one app's appsettings.json currently points at.</summary>
public sealed record AppConnectionState(
    GraniteApp App,
    string? ConnectionName,
    string? ConnectionString,
    string? Server,
    string? Database,
    string? User,
    bool IntegratedSecurity,
    string? Problem);

public static class AppConnectionReader
{
    public static AppConnectionState Read(GraniteApp app)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(app.AppSettingsPath);
            return FromBytes(app, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AppConnectionState(app, null, null, null, null, null, false, ex.Message);
        }
    }

    public static AppConnectionState FromBytes(GraniteApp app, byte[] bytes)
    {
        try
        {
            var found = AppSettingsConnectionEditor.Find(bytes);
            var pick = AppSettingsConnectionEditor.Pick(found, app.ConnectionName!);
            if (pick is null)
                return new AppConnectionState(app, null, null, null, null, null, false,
                    $"no \"{app.ConnectionName}\" connection string in appsettings.json");
            var cs = ConnectionStringEditor.Parse(pick.Value);
            return new AppConnectionState(app, pick.Name, pick.Value, cs.Server, cs.Database, cs.User, cs.IntegratedSecurity, null);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return new AppConnectionState(app, null, null, null, null, null, false, "appsettings.json couldn't be read: " + ex.Message);
        }
    }

    /// <summary>Case-insensitive comparison of SQL Server names, treating "." "(local)" and "localhost" as the same.</summary>
    public static string NormaliseServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) return string.Empty;
        string s = server.Trim();
        if (s.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        string instance = string.Empty;
        int slash = s.IndexOf('\\');
        if (slash >= 0) { instance = s[slash..]; s = s[..slash]; }
        if (s is "." or "(local)" || s.Equals("localhost", StringComparison.OrdinalIgnoreCase) || s == "127.0.0.1"
            || s.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            s = "(local)";
        return (s + instance).ToUpperInvariant();
    }
}
