using System.DirectoryServices.AccountManagement;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Lists local Windows user accounts so Panel 5's account field can offer a
/// dropdown instead of asking the installer to already know the exact
/// account name -- the same problem <see cref="SqlInstanceDiscovery"/>
/// solves for the server field on Panel 1.
/// </summary>
/// <remarks>
/// Deliberately local-machine only, not domain: querying a domain's user
/// list is a much bigger, slower, and more sensitive operation (potentially
/// thousands of accounts, needs AD query permissions) than this wizard has
/// any business doing. A domain account
/// (<c>DOMAIN\svc-account</c>) can still be typed into the field by hand --
/// this dropdown is a convenience for the common case (a dedicated local
/// service account, or the currently logged-on admin), not the only path.
/// </remarks>
public static class WindowsAccountDiscovery
{
    /// <summary>
    /// Enabled local user accounts, in the ".\name" form this wizard's
    /// account fields expect. Best-effort: any failure (locked-down
    /// machine, group policy restrictions) just means an empty list --
    /// the field stays a perfectly usable free-text box either way.
    /// </summary>
    public static List<string> GetLocalUserAccounts()
    {
        var results = new List<string>();
        try
        {
            using var context = new PrincipalContext(ContextType.Machine);
            using var queryFilter = new UserPrincipal(context);
            using var searcher = new PrincipalSearcher(queryFilter);

            foreach (Principal found in searcher.FindAll())
            {
                using (found)
                {
                    if (found is not UserPrincipal user) continue;
                    if (string.IsNullOrWhiteSpace(user.SamAccountName)) continue;
                    if (user.Enabled == false) continue; // skip disabled accounts; keep "unknown" (null)

                    results.Add($@".\{user.SamAccountName}");
                }
            }
        }
        catch
        {
            // Best-effort -- see remarks above.
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }
}
