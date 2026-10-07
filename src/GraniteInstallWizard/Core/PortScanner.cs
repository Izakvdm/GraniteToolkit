namespace GraniteInstallWizard.Core;

/// <summary>
/// Reads what's using ports on this server right now: IIS sites (appcmd),
/// the TCP listener table, and Windows' reserved port ranges (netsh).
/// The rules applied to the result live in <see cref="PortUse"/> and
/// <see cref="PortPlanner"/>, which are pure.
/// </summary>
public static class PortScanner
{
    public sealed record Scan(IReadOnlyList<IisSite> Sites, PortUse Use);

    public static async Task<Scan> ScanAsync(IEnumerable<string> replacedSiteNames, CancellationToken token)
    {
        var sites = await IisService.ListSitesAsync(token);
        var excluded = await ExcludedRangesAsync(token);
        return new Scan(sites, new PortUse(sites, LocalAddressDiscovery.ListeningTcpPorts(), excluded, replacedSiteNames));
    }

    /// <summary>
    /// Windows' reserved TCP port ranges. Best effort: an empty list if netsh
    /// can't be run. netsh is started by full path, not looked up on PATH.
    /// </summary>
    public static async Task<List<PortRange>> ExcludedRangesAsync(CancellationToken token)
    {
        try
        {
            string netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
            if (!File.Exists(netsh)) return new List<PortRange>();
            var r = await ProcessRunner.RunAsync(netsh, new[] { "interface", "ipv4", "show", "excludedportrange", "protocol=tcp" }, TimeSpan.FromSeconds(30), token);
            return r.ExitCode == 0 ? PortPlanner.ParseExcludedRanges(r.Output) : new List<PortRange>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new List<PortRange>();
        }
    }
}
