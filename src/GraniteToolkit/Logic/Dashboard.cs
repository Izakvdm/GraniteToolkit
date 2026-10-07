using Granite.Toolkit.Core.Addressing;
using Granite.Toolkit.Core.Custodian;
using Granite.Toolkit.Core.Discovery;

namespace GraniteToolkit.Logic;

/// <summary>Everything the dashboard reads from the server, gathered once per refresh (read-only).</summary>
public sealed record ServerSnapshot
{
    public string MachineName { get; init; } = "";
    public string OsDescription { get; init; } = "";

    public string? IisVersion { get; init; }
    public bool UrlRewrite { get; init; }
    public string? AspNetCore8 { get; init; }
    public bool AspNetCoreModule { get; init; }

    public IReadOnlyList<string> SqlInstances { get; init; } = Array.Empty<string>();

    public IReadOnlyList<GraniteInstall> Installs { get; init; } = Array.Empty<GraniteInstall>();

    /// <summary>Why IIS couldn't be read (appcmd failed), or null.</summary>
    public string? IisScanError { get; init; }

    /// <summary>"Site name (http port)" for each site running Granite Attach.</summary>
    public IReadOnlyList<string> AttachSites { get; init; } = Array.Empty<string>();

    /// <summary>Windows scheduled tasks the BI wizard created. Null when Task Scheduler couldn't be read.</summary>
    public IReadOnlyList<string>? BiSyncTasks { get; init; }

    /// <summary>
    /// Where each install's Web Desktop and Process App look for the
    /// Business API, with how that address fits this machine now. Keyed by
    /// the install's root folder.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<AddressFinding>> ApiAddresses { get; init; } =
        new Dictionary<string, IReadOnlyList<AddressFinding>>();

    /// <summary>
    /// Granite site bindings tied to one IP address instead of all of them,
    /// keyed by the install's root folder. Such a site stops answering when
    /// the IP changes and never answers over IPv6.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PinnedBinding>> PinnedBindings { get; init; } =
        new Dictionary<string, IReadOnlyList<PinnedBinding>>();

    /// <summary>
    /// Whether each install's Custodian can reach its process repository,
    /// as Custodian reports it. Keyed by the install's root folder; absent
    /// when the install has no Custodian or Web Desktop doesn't name it.
    /// </summary>
    public IReadOnlyDictionary<string, RepositoryCheck> CustodianRepositories { get; init; } =
        new Dictionary<string, RepositoryCheck>();

    /// <summary>NiFi services (NSSM running nifi.cmd), as NiFi Deploy installs them.</summary>
    public IReadOnlyList<NiFiService> NiFiServices { get; init; } = Array.Empty<NiFiService>();
}

public enum HealthState { Ok, Attention, Missing, Info }

/// <summary>One endpoint setting and how its address fits this machine.</summary>
public sealed record AddressFinding(EndpointSetting Setting, AddressHealth Health);

/// <summary>One line of the dashboard's server status list.</summary>
public sealed record StatusRow(string Area, HealthState State, string Title, string Detail);

/// <summary>What a module tile says about this server.</summary>
public sealed record ModuleStatus(ModuleId Id, string Line, bool Suggested);

/// <summary>Turns a snapshot into the dashboard's rows and tile hints. Pure, so the harness checks it.</summary>
public static class Dashboard
{
    public static IReadOnlyList<StatusRow> StatusRows(ServerSnapshot s)
    {
        var rows = new List<StatusRow>
        {
            new("Server", HealthState.Info, s.MachineName, s.OsDescription)
        };

        // Web server prerequisites
        rows.Add(s.IisVersion is null
            ? new("IIS", HealthState.Missing, "IIS not installed", "The Install module adds it from the release's own prerequisites.")
            : new("IIS", HealthState.Ok, $"IIS {s.IisVersion}", "Installed"));

        if (s.IisVersion is not null)
        {
            rows.Add(s.UrlRewrite
                ? new("IIS", HealthState.Ok, "URL Rewrite", "Installed")
                : new("IIS", HealthState.Missing, "URL Rewrite", "Not installed (Web Desktop needs it)"));
        }

        rows.Add((s.AspNetCore8, s.AspNetCoreModule) switch
        {
            (not null, true) => new("Runtime", HealthState.Ok, "ASP.NET Core 8 Hosting Bundle", $"Runtime {s.AspNetCore8} and the IIS module"),
            (not null, false) => new("Runtime", HealthState.Attention, "ASP.NET Core 8 Hosting Bundle", $"Runtime {s.AspNetCore8} found, but the IIS module is missing"),
            (null, true) => new("Runtime", HealthState.Attention, "ASP.NET Core 8 Hosting Bundle", "IIS module found, but no ASP.NET Core 8 runtime"),
            _ => new("Runtime", HealthState.Missing, "ASP.NET Core 8 Hosting Bundle", "Not installed")
        });

        rows.Add(s.SqlInstances.Count == 0
            ? new("SQL Server", HealthState.Info, "No local SQL Server", "Fine if the database is on another server")
            : new("SQL Server", HealthState.Ok, s.SqlInstances.Count == 1 ? "1 local instance" : $"{s.SqlInstances.Count} local instances", string.Join(", ", s.SqlInstances)));

        // Granite
        if (s.IisScanError is not null)
        {
            rows.Add(new("GraniteWMS", HealthState.Attention, "Couldn't read IIS", s.IisScanError));
        }
        else if (s.Installs.Count == 0)
        {
            rows.Add(new("GraniteWMS", s.IisVersion is null ? HealthState.Info : HealthState.Missing, "No GraniteWMS install found", "No IIS site holds a Granite app"));
        }
        else
        {
            foreach (var install in s.Installs)
            {
                string version = install.AppVersion is null ? "version unknown" : $"V{install.AppVersion}";
                string apps = string.Join(", ", install.Apps.Select(a => a.Title));
                bool complete = install.Apps.Count >= 4;
                rows.Add(new("GraniteWMS", complete ? HealthState.Ok : HealthState.Attention, $"{version} at {install.RootFolder}",
                    complete ? apps : $"{apps} (not all four core apps found)"));
                if (s.ApiAddresses.TryGetValue(install.RootFolder, out var findings) && findings.Count > 0)
                    rows.Add(AddressRow(findings));
                if (s.PinnedBindings.TryGetValue(install.RootFolder, out var pinned) && pinned.Count > 0)
                    rows.Add(PinnedRow(pinned));
                if (s.CustodianRepositories.TryGetValue(install.RootFolder, out var repo))
                    rows.Add(CustodianRow(repo));
            }
        }

        rows.Add(s.AttachSites.Count == 0
            ? new("Add-ons", HealthState.Info, "Granite Attach", "Not installed")
            : new("Add-ons", HealthState.Ok, "Granite Attach", string.Join(", ", s.AttachSites)));

        rows.Add(s.BiSyncTasks switch
        {
            null => new("Add-ons", HealthState.Info, "BI sync", "Couldn't read Task Scheduler"),
            { Count: 0 } => new("Add-ons", HealthState.Info, "BI sync", "No Windows scheduled task. BI may still run from SQL Agent or the Granite Scheduler; the BI module checks those."),
            var tasks => new("Add-ons", HealthState.Ok, "BI sync", "Scheduled task: " + string.Join(", ", tasks))
        });

        if (s.NiFiServices.Count == 0)
            rows.Add(new("Add-ons", HealthState.Info, "Apache NiFi", "Not installed"));
        foreach (var n in s.NiFiServices)
        {
            string what = $"{(n.Version is null ? "NiFi" : "NiFi " + n.Version)}, service {n.ServiceName}";
            rows.Add(n.IsRunning
                ? new("Add-ons", HealthState.Ok, "Apache NiFi", $"{what}, running ({n.NiFiHome})")
                : new("Add-ons", HealthState.Attention, "Apache NiFi", $"{what}, {(n.State is null ? "state unknown" : n.State.ToLowerInvariant())} ({n.NiFiHome})"));
        }

        return rows;
    }

    public static ModuleStatus ForModule(ModuleId id, ServerSnapshot s)
    {
        bool hasGranite = s.Installs.Count > 0;
        return id switch
        {
            ModuleId.Install => hasGranite
                ? new(id, $"{Plural(s.Installs.Count, "install")} found on this server. Use it for another install or a reinstall.", false)
                : new(id, "No GraniteWMS install found. Start here.", s.IisScanError is null),
            ModuleId.Bi => s.BiSyncTasks is { Count: > 0 }
                ? new(id, "A BI sync task is already scheduled here.", false)
                : new(id, hasGranite ? "GraniteWMS is installed. BI can be deployed against its database." : "Needs a GraniteWMS database to report on.", false),
            ModuleId.NiFi => s.NiFiServices.Count > 0
                ? new(id, "Already installed: " + string.Join(", ", s.NiFiServices.Select(n => $"service {n.ServiceName}{(n.IsRunning ? "" : " (not running)")}")), false)
                : new(id, hasGranite ? "Not installed yet. Needs the four downloads (NiFi, JDK, NSSM, JDBC driver)." : "Needs a GraniteWMS database to import into.", false),
            ModuleId.Address => AddressTile(s),
            ModuleId.DbSwitcher => new(id, hasGranite ? $"{Plural(s.Installs.Count, "install")} it can switch." : "Needs a local GraniteWMS install.", false),
            _ => new(id, "", false)
        };
    }

    /// <summary>The worst address problem first, in plain words.</summary>
    public static StatusRow AddressRow(IReadOnlyList<AddressFinding> findings)
    {
        var worst = findings.OrderByDescending(f => Severity(f.Health)).First();
        string hosts = string.Join(", ", findings.Select(f => f.Setting.Host).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase));
        return worst.Health switch
        {
            AddressHealth.NotThisMachine => new("GraniteWMS", HealthState.Missing, "Business API address is out of date",
                $"Web Desktop and Process App look for the API at {worst.Setting.Host}, which isn't this server's address any more. Use Change server address."),
            AddressHealth.Localhost => new("GraniteWMS", HealthState.Missing, "Business API address is localhost",
                "Works on the server only; PCs and scanners can't reach it. Use Change server address."),
            AddressHealth.Missing => new("GraniteWMS", HealthState.Attention, "Business API address not set", "An endpoint setting is empty."),
            AddressHealth.DhcpAddress => new("GraniteWMS", HealthState.Attention, $"Business API address {hosts}",
                "A DHCP address: it can change. Reserve it on the DHCP server, or switch to the server's name with Change server address."),
            AddressHealth.NameNotChecked => new("GraniteWMS", HealthState.Ok, $"Business API address {hosts}", "A DNS name (not checked from here)"),
            _ => new("GraniteWMS", HealthState.Ok, $"Business API address {hosts}", "This server")
        };
    }

    public static StatusRow PinnedRow(IReadOnlyList<PinnedBinding> pinned)
    {
        string list = string.Join(", ", pinned.Select(p => $"{p.App.Title} on {p.Binding.Address}:{p.Binding.Port}").Distinct());
        bool stale = pinned.Any(p => p.Health == AddressHealth.NotThisMachine);
        return stale
            ? new("GraniteWMS", HealthState.Missing, "Granite sites bound to an old IP address",
                $"{list}: that address isn't this server's any more, so these sites don't answer. Use Change server address to bind them to all addresses.")
            : new("GraniteWMS", HealthState.Attention, "Granite sites bound to one IP address",
                $"{list}: they stop answering if the IP changes, and never answer over IPv6. Use Change server address to bind them to all addresses.");
    }

    public static StatusRow CustodianRow(RepositoryCheck check) => check.Health switch
    {
        RepositoryHealth.Connected => new("GraniteWMS", HealthState.Ok, "Custodian process repository", "Connected"),
        RepositoryHealth.TokenRejected => new("GraniteWMS", HealthState.Missing, "Custodian can't reach the process repository",
            "Token rejected by GitHub. " + CustodianRepository.TokenAdvice),
        RepositoryHealth.Failed => new("GraniteWMS", HealthState.Missing, "Custodian can't reach the process repository", check.Message),
        _ => new("GraniteWMS", HealthState.Info, "Custodian process repository", "Not checked: " + check.Message)
    };

    private static int Severity(AddressHealth h) => h switch
    {
        AddressHealth.NotThisMachine => 5,
        AddressHealth.Localhost => 4,
        AddressHealth.Missing => 3,
        AddressHealth.DhcpAddress => 2,
        AddressHealth.NameNotChecked => 1,
        _ => 0
    };

    private static ModuleStatus AddressTile(ServerSnapshot s)
    {
        if (s.Installs.Count == 0) return new(ModuleId.Address, "Needs a GraniteWMS install.", false);
        var all = s.ApiAddresses.Values.SelectMany(f => f).ToList();
        var pinned = s.PinnedBindings.Values.SelectMany(p => p).ToList();
        if (all.Any(f => f.Health is AddressHealth.NotThisMachine or AddressHealth.Localhost))
            return new(ModuleId.Address, "The Business API address is out of date. Fix it here.", true);
        if (pinned.Any(p => p.Health == AddressHealth.NotThisMachine))
            return new(ModuleId.Address, "Granite sites are bound to an old IP address. Fix it here.", true);
        if (pinned.Count > 0)
            return new(ModuleId.Address, "Granite sites are bound to one IP address. Bind them to all addresses here.", false);
        if (all.Any(f => f.Health == AddressHealth.DhcpAddress))
            return new(ModuleId.Address, "Uses a DHCP address that can change. Switch to the server's name here if it isn't reserved.", false);
        return new(ModuleId.Address, "The Business API address matches this server.", false);
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}

/// <summary>Reads schtasks /query /fo csv /nh output for the BI wizard's tasks.</summary>
public static class BiTaskParser
{
    /// <summary>The BI wizard names its tasks "GraniteWMS BI Sync - &lt;BI database&gt;" (DeploymentContext.TaskName).</summary>
    public const string Prefix = "GraniteWMS BI Sync - ";

    public static IReadOnlyList<string> Parse(string csv)
    {
        var names = new List<string>();
        foreach (string raw in csv.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith('"')) continue;
            int end = line.IndexOf('"', 1);
            if (end < 0) continue;
            string name = line[1..end].TrimStart('\\');
            int slash = name.LastIndexOf('\\');
            string leaf = slash >= 0 ? name[(slash + 1)..] : name;
            if (leaf.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && !names.Contains(leaf, StringComparer.OrdinalIgnoreCase))
                names.Add(leaf);
        }
        return names;
    }
}
