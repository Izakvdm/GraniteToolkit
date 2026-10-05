using System.Text.Json;
using GraniteNiFiDeploy.Models;

namespace GraniteNiFiDeploy.Core;

/// <summary>
/// Runs the whole deployment in order, logging each stage:
/// 1 checks, 2 SQL (framework, feeds, NiFi's login), 3 NiFi files,
/// 4 import folders, 5 service, 6 flow via NiFi's API, 7 record.
/// SQL goes first: it's where permissions most often fail, and nothing on
/// disk has changed yet if it does. A failure in stages 3 to 5 removes the
/// service and NiFi folder this run created, so the wizard can simply be
/// run again.
/// </summary>
public sealed class DeployRunner
{
    private readonly Action<LogEntry> _log;

    public DeployRunner(Action<LogEntry> log) => _log = log;

    /// <summary>Warnings the operator should read after a successful run (e.g. NiFi couldn't reach SQL yet).</summary>
    public List<string> FollowUps { get; } = new();

    public async Task RunAsync(DeployContext c, CancellationToken token)
    {
        if (c.Media is null) throw new InvalidOperationException("Install media not checked (Step 1).");

        // ---- 1. checks
        Stage("Checks");
        var problems = NiFiInstaller.PreflightProblems(c);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(" ", problems));
        var sql = new SqlDeployer(_log);
        var (check, error) = await sql.CheckAsync(c.ConnectionString, token);
        if (check is null) throw new InvalidOperationException("Can't connect to SQL Server: " + error);
        if (check.Blockers.Count > 0) throw new InvalidOperationException(string.Join(" ", check.Blockers));
        string jdbcUrl = JdbcUrl.Build(c.SqlServerInstance, c.DatabaseName, c.ValidateSqlCertificate);
        _log(new LogEntry(LogLevel.Success, $"SQL Server {check.Version}, database {c.DatabaseName}: ready"));
        _log(new LogEntry(LogLevel.Detail, "NiFi will connect with " + jdbcUrl));

        // ---- 2. SQL
        Stage("Granite database");
        await sql.DeployScriptsAsync(c.ConnectionString, c.Feeds, c.SalesOrderDefaults, c.PurchaseOrderDefaults, token);
        string nifiSqlPassword = InputRules.GeneratePassword();
        await sql.EnsureNiFiLoginAsync(c.ConnectionString, c.NiFiSqlLogin, nifiSqlPassword, token);
        var missing = await sql.MissingObjectsAsync(c.ConnectionString, c.Feeds, token);
        if (missing.Count > 0) throw new InvalidOperationException("These objects are missing after the scripts ran: " + string.Join(", ", missing));
        _log(new LogEntry(LogLevel.Success, "Import framework and feeds deployed"));

        // ---- 3 to 5. NiFi on disk and as a service (rolled back on failure)
        var installer = new NiFiInstaller(_log);
        try
        {
            Stage("NiFi");
            await installer.InstallFilesAsync(c, token);
            Stage("Import folders");
            installer.CreateImportFolders(c);
            Stage("Windows service");
            await installer.InstallServiceAsync(c, token);
            await installer.StartServiceAsync(c, token);
        }
        catch (Exception ex)
        {
            _log(new LogEntry(LogLevel.Error, ex.Message));
            Stage("Undoing this run");
            await installer.RollBackAsync(c);
            _log(new LogEntry(LogLevel.Info, "The database objects and the SQL login stay (re-running the wizard updates them)."));
            throw;
        }

        // ---- 6. flow, through NiFi's API
        Stage("Granite CSV import flow");
        try
        {
            await ConfigureFlowAsync(c, jdbcUrl, nifiSqlPassword, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FollowUps.Add($"NiFi is installed and running, but setting up the flow stopped: {ex.Message} " +
                          $"Finish it by hand at {c.NiFiUrl}: upload GraniteCsvImport.json from the NiFiDeploy kit and set its parameters (README step 4).");
            throw;
        }

        // ---- 7. record
        Stage("Done");
        WriteRecord(c, jdbcUrl, check);
    }

    private async Task ConfigureFlowAsync(DeployContext c, string jdbcUrl, string sqlPassword, CancellationToken token)
    {
        string thumbprint = NiFiApiClient.KeystoreThumbprint(c.NiFiHome);
        _log(new LogEntry(LogLevel.Detail, $"NiFi's certificate (SHA-256) {thumbprint}: only this certificate is accepted"));
        using var api = new NiFiApiClient(new Uri($"https://localhost:{c.Port}/"), NiFiApiClient.PinnedHandler(thumbprint), _log);

        await api.LoginAsync(c.AdminUser, c.AdminPassword, TimeSpan.FromMinutes(5), token);
        _log(new LogEntry(LogLevel.Success, $"Signed in to NiFi as {c.AdminUser}"));

        string root = await api.RootGroupIdAsync(token);
        if (await api.FindChildGroupAsync(root, NiFiFlowParameters.FlowGroupName, token) is not null)
            throw new InvalidOperationException($"NiFi already has a \"{NiFiFlowParameters.FlowGroupName}\" group. This is a fresh install, so that shouldn't happen; check {c.NiFiUrl}.");

        var existingContexts = await api.ParameterContextIdsAsync(token);
        var (group, contextId) = await api.UploadFlowAsync(root, NiFiFlowParameters.FlowGroupName, DeployScripts.ReadFlow(), token);
        if (contextId is null) throw new InvalidDataException("The uploaded flow has no parameter context.");
        if (existingContexts.Contains(contextId))
            throw new InvalidOperationException($"NiFi attached the flow to an existing \"{NiFiFlowParameters.ContextName}\" parameter context. Its settings won't be overwritten; check {c.NiFiUrl}.");
        _log(new LogEntry(LogLevel.Success, "Flow uploaded"));

        var parameters = NiFiFlowParameters.Build(jdbcUrl, c.NiFiSqlLogin, sqlPassword, c.DriverFolder, c.InboundFolder, c.ArchiveFolder, c.ErrorFolder);
        await api.UpdateParametersAsync(contextId, parameters, token);
        _log(new LogEntry(LogLevel.Success, "Parameters set (database, login, folders). The SQL password is stored encrypted by NiFi."));

        var services = await api.ControllerServicesAsync(group, token);
        var pool = services.FirstOrDefault(s => s.Type.EndsWith(".DBCPConnectionPool", StringComparison.Ordinal));
        if (pool.Id is { Length: > 0 })
        {
            _log(new LogEntry(LogLevel.Info, "Asking NiFi to test its connection to SQL Server"));
            var flowPool = DeployScripts.FlowServiceProperties(DeployScripts.ReadFlow(), ".DBCPConnectionPool");
            var results = await api.VerifyControllerServiceAsync(pool.Id, flowPool, token);
            foreach (var r in results)
                _log(new LogEntry(r.Failed ? LogLevel.Warning : LogLevel.Detail, $"{r.Step}: {r.Outcome}{(r.Explanation.Length > 0 ? " - " + r.Explanation : "")}"));
            bool loginRefused = results.Any(r => r.Failed && r.Explanation.Contains("Login failed", StringComparison.OrdinalIgnoreCase));
            if (loginRefused)
            {
                // Reaching "Login failed" means the network path works; it's the sign-in.
                FollowUps.Add($"SQL Server refused NiFi's login {c.NiFiSqlLogin}. Its reason (the 18456 state) is in the SQL Server error log: in SSMS run EXEC xp_readerrorlog 0, 1, N'{c.NiFiSqlLogin}'. " +
                              "State 8 is a wrong password (run this wizard again to reset it), 58 means the server only allows Windows logins (turn on SQL Server and Windows Authentication mode, then restart SQL Server), 7 means the login is disabled.");
                _log(new LogEntry(LogLevel.Warning, FollowUps[^1]));
            }
            else if (results.Any(r => r.Failed))
            {
                string hint = JdbcUrl.Parse(c.SqlServerInstance).Instance is not null && JdbcUrl.Parse(c.SqlServerInstance).Port is null
                    ? " For a named instance NiFi needs the SQL Server Browser service running, or give the server as NAME,PORT."
                    : "";
                FollowUps.Add("NiFi couldn't connect to SQL Server yet. Check that TCP/IP is enabled for the instance (SQL Server Configuration Manager) and the firewall allows it." + hint +
                              " Imports will wait and retry; once fixed, nothing else needs doing.");
                _log(new LogEntry(LogLevel.Warning, FollowUps[^1]));
            }
            else
            {
                _log(new LogEntry(LogLevel.Success, "NiFi connected to the Granite database"));
            }
        }

        await api.EnableControllerServicesAsync(group, token);
        _log(new LogEntry(LogLevel.Success, "Controller services enabled"));
        await api.StartGroupAsync(group, token);
        await Task.Delay(TimeSpan.FromSeconds(3), token);
        var problems = await api.ProblemsAsync(group, token);
        if (problems.Count > 0)
        {
            foreach (var p in problems) _log(new LogEntry(LogLevel.Warning, $"{p.Group} / {p.Name}: {p.State} {p.Detail}"));
            FollowUps.Add($"{problems.Count} part(s) of the flow aren't running. Open {c.NiFiUrl} to see why.");
        }
        else
        {
            _log(new LogEntry(LogLevel.Success, "Flow running"));
        }
    }

    private void WriteRecord(DeployContext c, string jdbcUrl, SqlServerCheck check)
    {
        try
        {
            SecureFolders.EnsureAdminOnly(MediaStaging.DataRoot);
            string folder = Path.Combine(MediaStaging.DataRoot, "Deployments");
            SecureFolders.EnsureAdminOnly(folder);
            var record = new
            {
                deployed = DateTime.Now.ToString("o"),
                by = $"{Environment.UserDomainName}\\{Environment.UserName}",
                module = typeof(DeployRunner).Assembly.GetName().Version?.ToString(3),
                nifi = new { version = c.Media!.NiFiVersion, home = c.NiFiHome, service = c.ServiceName, url = c.NiFiUrl, user = c.AdminUser, java = c.Media.JavaVersion, jdbc = c.Media.JdbcJarName },
                sql = new { server = c.SqlServerInstance, database = c.DatabaseName, version = check.Version, nifiLogin = c.NiFiSqlLogin, jdbcUrl },
                import = new { root = c.ImportRoot, inbound = c.InboundFolder, archive = c.ArchiveFolder, error = c.ErrorFolder, feeds = c.Feeds.OrderBy(f => f).ToArray(), dropAccount = c.DropAccount },
                orderDefaults = new { salesOrder = c.SalesOrderDefaults, purchaseOrder = c.PurchaseOrderDefaults },
                followUps = FollowUps
            };
            string path = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}_{c.ServiceName}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
            _log(new LogEntry(LogLevel.Info, $"Deployment record (no passwords): {path}"));
        }
        catch (Exception ex)
        {
            _log(new LogEntry(LogLevel.Warning, "Couldn't write the deployment record: " + ex.Message));
        }
    }

    private void Stage(string text) => _log(new LogEntry(LogLevel.Stage, text));
}
