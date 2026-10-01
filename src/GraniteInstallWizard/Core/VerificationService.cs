using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.Core;

/// <summary>
/// After the install: can the app login read the database, and does each
/// site answer over HTTPS?
/// </summary>
public sealed class VerificationService
{
    private readonly Action<LogEntry> _log;

    public VerificationService(Action<LogEntry> log) => _log = log;

    private void Log(LogLevel level, string message) => _log(new LogEntry(level, message));

    /// <summary>Returns one line per check for the summary, and whether any failed.</summary>
    public async Task<(List<string> Lines, bool AnyFailed)> VerifyAsync(InstallContext c, CancellationToken token)
    {
        var lines = new List<string>();
        bool failed = false;

        try
        {
            await using var conn = await SqlConnectionFactory.OpenAsync(SqlConnectionFactory.AppLogin(c, c.DatabaseName), token);
            object? n = await SqlConnectionFactory.ScalarAsync(conn, "SELECT COUNT(*) FROM dbo.SystemSettings;", token);
            Log(LogLevel.Success, $"Database: login {c.AppLogin} can read {c.DatabaseName} ({n} system settings).");
            lines.Add("Database login: OK");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Database check failed for login {c.AppLogin}: {ex.Message}");
            lines.Add("Database login: FAILED");
            failed = true;
        }

        // The certificate may not be trusted by this process yet (and an
        // existing one may not list localhost), so certificate validation is
        // skipped for this local smoke test only.
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        // The first request makes IIS start the app, which can take a while.
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

        foreach (var comp in c.EnabledComponents)
        {
            string url = c.UrlFor(comp.Key, "localhost") + comp.VerifyPath;
            var (code, error) = await ProbeAsync(http, comp.Title, url, token);
            string where = $"Check the Granite*.log files in {c.InstallPathFor(comp)} and the Windows Application event log (source: IIS AspNetCore Module V2).";
            if (code is null)
            {
                Log(LogLevel.Error, $"{comp.Title}: {url} did not respond ({error}). {where}");
                lines.Add($"{comp.Title}: no response");
                failed = true;
            }
            else if (code >= 500)
            {
                Log(LogLevel.Error, $"{comp.Title}: {url} still returned HTTP {code} after waiting for it to start. {where}");
                lines.Add($"{comp.Title}: HTTP {code}");
                failed = true;
            }
            else
            {
                Log(LogLevel.Success, $"{comp.Title}: {url} responded HTTP {code}.");
                lines.Add($"{comp.Title}: HTTP {code}");
            }
        }

        return (lines, failed);
    }

    /// <summary>
    /// A freshly started ASP.NET Core app answers 502/503 (or not at all)
    /// for a few seconds while IIS brings it up, so a 5xx or no answer is
    /// retried every 10 seconds for about a minute before it counts. The
    /// first real run (v0.2.0) reported two healthy sites as failed because
    /// it asked exactly once, 4 seconds after they started.
    /// </summary>
    private async Task<(int? Code, string Error)> ProbeAsync(HttpClient http, string title, string url, CancellationToken token)
    {
        const int attempts = 7;
        int? code = null;
        string error = string.Empty;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url, token);
                code = (int)response.StatusCode;
                error = string.Empty;
                if (code < 500) return (code, error);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (token.IsCancellationRequested) throw;
                code = null;
                error = ex.Message;
            }
            if (attempt < attempts)
            {
                Log(LogLevel.Detail, $"{title}: {(code is null ? "no response yet" : $"HTTP {code}")}, still starting; retrying in 10 seconds ({attempt}/{attempts - 1}).");
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            }
        }
        return (code, error);
    }
}
