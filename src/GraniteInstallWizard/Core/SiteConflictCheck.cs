using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.Core;

/// <summary>
/// Pre-flight's IIS rules: which existing sites and ports block the
/// install, and which are fine because Step 4's "Replace existing IIS
/// sites" is ticked. Pure, so the LogicHarness can check every case.
/// </summary>
/// <remarks>
/// Added in v0.3.2. Before that the wizard was fresh-install only, and a
/// dry run on a machine that already had the first install's four sites
/// stopped with seven problems and no way through except removing the
/// sites by hand. Replacing is limited to sites whose names match the
/// ones on Step 4: a port held by any other site, or by a non-IIS
/// program, still blocks.
/// </remarks>
public static class SiteConflictCheck
{
    public sealed record Result(List<string> Errors, List<string> Warnings, List<IisSite> SitesToReplace);

    /// <param name="excludedRanges">Windows' reserved port ranges (v0.7.0); IIS can't bind inside them.</param>
    public static Result Evaluate(IReadOnlyList<IisSite> existingSites, ISet<int> listeningPorts, InstallContext c, IReadOnlyList<PortRange>? excludedRanges = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        var ourNames = c.EnabledComponents.Select(x => c.Sites[x.Key].SiteName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toReplace = c.ReplaceExistingSites
            ? existingSites.Where(s => ourNames.Contains(s.Name)).ToList()
            : new List<IisSite>();
        var replacedNames = toReplace.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var replacedPorts = toReplace.SelectMany(s => s.Bindings).Select(b => b.Port).ToHashSet();

        foreach (var comp in c.EnabledComponents)
        {
            var s = c.Sites[comp.Key];

            var sameName = existingSites.FirstOrDefault(x => string.Equals(x.Name, s.SiteName, StringComparison.OrdinalIgnoreCase));
            if (sameName is not null)
            {
                if (c.ReplaceExistingSites)
                    warnings.Add($"The existing IIS site \"{s.SiteName}\" and its app pool will be removed and recreated.");
                else
                    errors.Add($"An IIS site called \"{s.SiteName}\" already exists. Tick \"Replace existing IIS sites\" on Step 4 to reinstall over it, or choose another site name.");
            }

            // A port held by the same-named site is already covered by the
            // name message above (and freed if that site is replaced).
            var clash = existingSites.FirstOrDefault(x => !replacedNames.Contains(x.Name)
                && !string.Equals(x.Name, s.SiteName, StringComparison.OrdinalIgnoreCase)
                && x.Bindings.Any(b => b.Port == s.Port));
            if (clash is not null)
                errors.Add($"Port {s.Port} ({comp.Title}) is already used by the IIS site \"{clash.Name}\", which isn't being replaced. Choose another port on Step 4.");
            else if (!replacedPorts.Contains(s.Port) && listeningPorts.Contains(s.Port) && !existingSites.Any(x => x.Bindings.Any(b => b.Port == s.Port)))
                errors.Add($"Port {s.Port} ({comp.Title}) is already in use by another program on this server.");

            if (excludedRanges?.FirstOrDefault(r => r.Contains(s.Port)) is { Start: > 0 } reserved)
                errors.Add($"Port {s.Port} ({comp.Title}) is reserved by Windows (excluded range {reserved}, usually Hyper-V, WSL or Docker), so IIS can't use it. Use \"Find free ports\" on Step 4.");
        }

        return new Result(errors, warnings, toReplace);
    }
}
