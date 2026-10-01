using System.Net.Http;
using GraniteDbSwitcher.Models;
using Microsoft.Data.SqlClient;

namespace GraniteDbSwitcher.Core;

public sealed class SwitchOptions
{
    public bool FixLoginAccess { get; init; } = true;
    public bool RecyclePools { get; init; } = true;
    public bool WarmUp { get; init; } = true;
}

/// <summary>
/// Points every database-connected app in one install at another database:
/// fix login access, rewrite the connection strings, check the apps' login
/// can open the database, recycle the pools, and wait for the apps to answer.
/// </summary>
public sealed class SwitchRunner
{
    public const string BackupSuffix = ".before-dbswitcher";

    private readonly Action<LogEntry> _log;

    public SwitchRunner(Action<LogEntry> log) => _log = log;

    private void Log(LogLevel level, string message) => _log(new LogEntry(level, message));

    public async Task<bool> RunAsync(GraniteInstall install, SqlAdminIdentity admin, string targetDb, SwitchOptions options, CancellationToken token)
    {
        Log(LogLevel.Stage, $"Switching {install.RootFolder} to {targetDb}");

        var states = install.DatabaseApps.Select(AppConnectionReader.Read).ToList();
        foreach (var bad in states.Where(s => s.Problem is not null))
            throw new InvalidOperationException($"{bad.App.Title}: {bad.Problem}");
        if (states.Count == 0)
            throw new InvalidOperationException("No Business API, Custodian or Process App was found in this install.");

        // The apps normally share one login; fix access for each distinct one.
        if (options.FixLoginAccess)
        {
            await using var c = await SqlAccess.OpenAsync(SqlAccess.Admin(admin), token);
            foreach (string login in states.Where(s => !s.IntegratedSecurity && !string.IsNullOrWhiteSpace(s.User))
                                           .Select(s => s.User!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await LoginAccess.EnsureAsync(c, targetDb, login, _log, token);
            }
            if (states.Any(s => s.IntegratedSecurity))
                Log(LogLevel.Warning, "An app connects with Windows authentication (its app pool identity); the switcher doesn't change database access for that.");

            string? clr = await LoginAccess.ClrWarningAsync(c, targetDb, token);
            if (clr is not null) Log(LogLevel.Warning, clr);
        }

        // Prove the apps' own login can open the target before touching any file.
        foreach (var group in states.GroupBy(s => ConnectionStringFor(s, targetDb)))
        {
            string titles = string.Join(", ", group.Select(s => s.App.Title));
            try
            {
                await using var check = await SqlAccess.OpenAsync(SqlAccess.ForCheck(group.Key), token);
                await SqlAccess.ScalarAsync(check, "SELECT COUNT(*) FROM dbo.SystemSettings;", token);
                Log(LogLevel.Success, $"{titles}: the apps' login can read {targetDb}.");
            }
            catch (SqlException ex)
            {
                throw new InvalidOperationException(
                    $"{titles} wouldn't be able to use {targetDb} with their login ({ex.Message}). Nothing was changed.", ex);
            }
        }

        // Rewrite the files, all or nothing.
        var originals = new List<(string Path, byte[] Bytes)>();
        try
        {
            foreach (var s in states)
            {
                string path = s.App.AppSettingsPath;
                byte[] before = await File.ReadAllBytesAsync(path, token);
                var target = AppSettingsConnectionEditor.Pick(AppSettingsConnectionEditor.Find(before), s.App.ConnectionName!)!;
                string newValue = ConnectionStringFor(s, targetDb);
                if (newValue == target.Value)
                {
                    Log(LogLevel.Info, $"{s.App.Title}: already on {targetDb}.");
                    continue;
                }

                string backup = path + BackupSuffix;
                if (!File.Exists(backup)) await File.WriteAllBytesAsync(backup, before, token);

                byte[] after = AppSettingsConnectionEditor.Replace(before, target, newValue);
                originals.Add((path, before));
                await File.WriteAllBytesAsync(path, after, token);
                Log(LogLevel.Success, $"{s.App.Title}: {s.Database ?? "(no database)"} -> {targetDb}");
            }
        }
        catch
        {
            foreach (var (path, bytes) in originals)
            {
                try { File.WriteAllBytes(path, bytes); } catch { /* reported below */ }
            }
            Log(LogLevel.Error, "Writing appsettings.json failed; the files already changed were put back.");
            throw;
        }

        if (options.RecyclePools)
        {
            var poolStates = await IisService.PoolStatesAsync(token);
            foreach (string pool in states.Select(s => s.App.AppPool).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                poolStates.TryGetValue(pool, out string? state);
                await IisService.RecycleOrStartAsync(pool, state, _log, token);
            }
            Log(LogLevel.Success, "App pools recycled.");
        }
        else
        {
            Log(LogLevel.Warning, "App pools weren't recycled: the apps keep using the old database until they restart.");
        }

        bool allUp = true;
        if (options.RecyclePools && options.WarmUp)
        {
            foreach (var s in states)
                allUp &= await WarmUpAsync(s.App, token);
        }

        Log(allUp ? LogLevel.Success : LogLevel.Warning,
            allUp ? $"Done. {install.RootFolder} is on {targetDb}. Sign in to Web Desktop again; the old session belongs to the previous database."
                  : $"Switched to {targetDb}, but not every app answered yet. Check the app's stdout log or Event Viewer if it stays down.");
        return allUp;
    }

    /// <summary>The app's current connection string with only the database changed.</summary>
    public static string ConnectionStringFor(AppConnectionState s, string targetDb) =>
        ConnectionStringEditor.WithDatabase(s.ConnectionString!, targetDb);

    /// <summary>Requests the site root until it answers with anything but a 5xx, for up to a minute.</summary>
    private async Task<bool> WarmUpAsync(GraniteApp app, CancellationToken token)
    {
        string? url = app.LocalUrl;
        if (url is null) return true;

        using var handler = new HttpClientHandler
        {
            // Local check only; the sites use the machine's self-signed certificate.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        var deadline = DateTime.UtcNow.AddSeconds(60);
        string last = "no answer";
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var response = await http.GetAsync(url, token);
                int code = (int)response.StatusCode;
                if (code < 500)
                {
                    Log(LogLevel.Success, $"{app.Title} is up ({url}, HTTP {code}).");
                    return true;
                }
                last = $"HTTP {code}";
            }
            catch (HttpRequestException ex) { last = ex.Message; }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { last = "timed out"; }
            await Task.Delay(2000, token);
        }
        Log(LogLevel.Warning, $"{app.Title} didn't answer at {url} within a minute ({last}).");
        return false;
    }
}
