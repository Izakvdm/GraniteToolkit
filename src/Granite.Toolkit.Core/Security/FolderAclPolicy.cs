namespace Granite.Toolkit.Core.Security;

/// <summary>One access rule on a folder, reduced to what the policy needs.</summary>
/// <param name="Sid">The identity's SID string, e.g. S-1-5-32-545 for BUILTIN\Users.</param>
/// <param name="Rights">The raw access mask (FileSystemRights as an int).</param>
/// <param name="Allow">Allow rule (true) or deny rule (false).</param>
/// <param name="InheritOnly">Applies only to children, not to the folder itself.</param>
public sealed record AclRule(string Sid, int Rights, bool Allow, bool InheritOnly);

/// <summary>
/// Pure rules about who may own and change the toolkit's folders, so the
/// harness can check them without Windows. Used for the toolkit's data
/// folder under ProgramData (logs, saved settings) and to warn when the
/// toolkit itself runs from a folder ordinary users can change.
/// </summary>
/// <remarks>
/// Why it matters: the toolkit runs as administrator. Anything an ordinary
/// user can write into the folders it reads from (a replaced exe or DLL, a
/// doctored settings file) would run, or be trusted, with administrator
/// rights. ProgramData lets any user create folders by default, so the
/// toolkit also refuses a data folder that a non-administrator created
/// first and could have seeded.
/// </remarks>
public static class FolderAclPolicy
{
    public const string Administrators = "S-1-5-32-544";
    public const string LocalSystem = "S-1-5-18";
    public const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>Broad groups that every ordinary user is in.</summary>
    public static readonly IReadOnlyDictionary<string, string> BroadGroups = new Dictionary<string, string>
    {
        ["S-1-1-0"] = "Everyone",
        ["S-1-5-11"] = "Authenticated Users",
        ["S-1-5-32-545"] = "Users",
        ["S-1-5-4"] = "INTERACTIVE",
        ["S-1-5-32-546"] = "Guests",
        ["S-1-5-7"] = "ANONYMOUS LOGON"
    };

    /// <summary>Any right that changes the folder's contents or its permissions.</summary>
    public const int WriteRightsMask =
        0x0002      // WriteData / CreateFiles
        | 0x0004    // AppendData / CreateDirectories
        | 0x0010    // WriteExtendedAttributes
        | 0x0040    // DeleteSubdirectoriesAndFiles
        | 0x0100    // WriteAttributes
        | 0x10000   // Delete
        | 0x40000   // ChangePermissions
        | 0x80000   // TakeOwnership
        | 0x10000000 // GENERIC_ALL
        | 0x40000000; // GENERIC_WRITE

    public static bool IsTrustedOwner(string? sid) =>
        sid is Administrators or LocalSystem or TrustedInstaller;

    /// <summary>
    /// Broad groups with an allow rule that lets them change the folder or
    /// what's in it (inherit-only rules count: they apply to the files the
    /// toolkit reads). Deny rules aren't subtracted, to stay conservative.
    /// </summary>
    public static IReadOnlyList<string> UntrustedWriters(IEnumerable<AclRule> rules) =>
        rules
            .Where(r => r.Allow && (r.Rights & WriteRightsMask) != 0 && BroadGroups.ContainsKey(r.Sid))
            .Select(r => BroadGroups[r.Sid])
            .Distinct()
            .ToList();
}
