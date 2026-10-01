using GraniteAttachInstaller.Models;
using Microsoft.Data.SqlClient;

namespace GraniteAttachInstaller.Core;

/// <summary>
/// The database stage: the three storage tables, then - if a process name
/// was given on Step 2 - the process-step kit's stored procedures resolved
/// for that name, plus the resolved WebTemplate HTML file.
/// </summary>
public sealed class AttachDatabaseInstaller
{
    private readonly Action<LogEntry> _log;
    public AttachDatabaseInstaller(Action<LogEntry> log) => _log = log;
    private void Log(LogLevel level, string message) => _log(new LogEntry(level, message));

    public async Task<(bool ok, string? error)> TestConnectionAsync(InstallContext c, CancellationToken token)
    {
        try
        {
            await using var conn = new SqlConnection(c.ConnectionString);
            await conn.OpenAsync(token);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Lists databases on the server for Step 1's "existing databases" helper. Best-effort - an empty list just means type the name in by hand.</summary>
    public async Task<List<string>> ListDatabasesAsync(InstallContext c, CancellationToken token)
    {
        var names = new List<string>();
        try
        {
            string masterConnString = c.UseSqlAuth
                ? $"Server={c.SqlServerInstance};Database=master;User Id={c.SqlUser};Password={c.SqlPassword};TrustServerCertificate=True;"
                : $"Server={c.SqlServerInstance};Database=master;Trusted_Connection=True;TrustServerCertificate=True;";
            await using var conn = new SqlConnection(masterConnString);
            await conn.OpenAsync(token);
            using var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name;", conn);
            await using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) names.Add(reader.GetString(0));
        }
        catch { /* best-effort; Step 1's database field stays a free-text box either way */ }
        return names;
    }

    public async Task RunAsync(InstallContext c, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(c.SqlFolder) || !Directory.Exists(c.SqlFolder))
            throw new InvalidOperationException($"Can't find the sql folder at '{c.SqlFolder}'.");

        await using var conn = new SqlConnection(c.ConnectionString);
        await conn.OpenAsync(token);

        Log(LogLevel.Stage, "Creating storage tables...");
        foreach (string file in new[]
                 {
                     "02_Attach_Table.sql",
                     "03_AttachAudit_Table.sql",
                     "04_AttachPending_Table.sql"
                 })
        {
            string path = Path.Combine(c.SqlFolder, file);
            if (!File.Exists(path)) throw new FileNotFoundException($"Missing {file} in {c.SqlFolder}.", path);
            Log(LogLevel.Detail, $"Running {file} ...");
            await SqlScriptRunner.RunFileAsync(conn, path, token);
        }
        Log(LogLevel.Success, "Tables created (or already existed).");

        if (string.IsNullOrWhiteSpace(c.ProcessName))
        {
            Log(LogLevel.Info, "No process name given - stopping after the tables. Run this installer again later with a process name to also set up the process-step kit.");
            return;
        }

        Log(LogLevel.Stage, $"Setting up the process-step kit for process '{c.ProcessName}' ...");
        string resolvedFolder = Path.Combine(c.SqlFolder, $"resolved_{c.ProcessName}");
        Directory.CreateDirectory(resolvedFolder);

        string attachStart = ResolveProcessTemplate(Path.Combine(c.SqlFolder, "Prescript_AttachStart_Template.sql"), c.ProcessName);
        string attachStartPath = Path.Combine(resolvedFolder, $"Prescript_{c.ProcessName}_AttachStart.sql");
        await File.WriteAllTextAsync(attachStartPath, attachStart, token);
        await SqlScriptRunner.RunScriptAsync(conn, attachStart, token);
        Log(LogLevel.Detail, $"Created dbo.Prescript_{c.ProcessName}_AttachStart");

        string attachShow = ResolveProcessTemplate(Path.Combine(c.SqlFolder, "Prescript_AttachShow_Template.sql"), c.ProcessName);
        string attachShowPath = Path.Combine(resolvedFolder, $"Prescript_{c.ProcessName}_AttachShow.sql");
        await File.WriteAllTextAsync(attachShowPath, attachShow, token);
        await SqlScriptRunner.RunScriptAsync(conn, attachShow, token);
        Log(LogLevel.Detail, $"Created dbo.Prescript_{c.ProcessName}_AttachShow");

        string step200 = ResolveProcessTemplate(Path.Combine(c.SqlFolder, "Prescript_PostExec_ReconcileAttachToken_Template.sql"), c.ProcessName)
            .Replace("Step2XX", "Step200");
        string step200Path = Path.Combine(resolvedFolder, $"Prescript_{c.ProcessName}_Step200.sql");
        await File.WriteAllTextAsync(step200Path, step200, token);
        await SqlScriptRunner.RunScriptAsync(conn, step200, token);
        Log(LogLevel.Detail, $"Created dbo.Prescript_{c.ProcessName}_Step200");

        string webTemplateSrcPath = Path.Combine(c.SqlFolder, "WebTemplate_AttachShow_Template.html");
        if (File.Exists(webTemplateSrcPath))
        {
            string webTemplate = await File.ReadAllTextAsync(webTemplateSrcPath, token);
            string appHost = ExtractHost(c.PublicBaseUrl);
            webTemplate = webTemplate.Replace("REPLACE_WITH_APP_HOST", appHost);
            string webPath = Path.Combine(resolvedFolder, $"WebTemplate_{c.ProcessName}_AttachShow.html");
            await File.WriteAllTextAsync(webPath, webTemplate, token);
            c.ResolvedWebTemplatePath = webPath;
            Log(LogLevel.Success, $"Process-step kit ready. Files in: {resolvedFolder}");
        }
        else
        {
            Log(LogLevel.Warning, $"WebTemplate_AttachShow_Template.html not found in {c.SqlFolder} - stored procedures were still created; paste the WebTemplate manually from the generic template.");
        }
    }

    private static string ResolveProcessTemplate(string templatePath, string processName)
    {
        if (!File.Exists(templatePath)) throw new FileNotFoundException("Missing process-step kit template.", templatePath);
        return File.ReadAllText(templatePath).Replace("<Process>", processName);
    }

    private static string ExtractHost(string publicBaseUrl)
    {
        if (Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var uri)) return uri.Host;
        // Not a full URL (e.g. just an IP was typed) - strip a trailing port if present.
        int colon = publicBaseUrl.LastIndexOf(':');
        return colon > 0 ? publicBaseUrl[..colon] : publicBaseUrl;
    }
}
