using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Granite.Toolkit.Core.Security;

/// <summary>Where the toolkit keeps its own files outside Program Files.</summary>
public static class ToolkitPaths
{
    /// <summary>C:\ProgramData\Granite Toolkit: administrators and SYSTEM only.</summary>
    public static string DataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Granite Toolkit");

    public static string Logs => Path.Combine(DataRoot, "Logs");

    /// <summary>The folder the running exe is in (the install folder, or wherever a portable copy was unpacked).</summary>
    public static string AppFolder => AppContext.BaseDirectory.TrimEnd('\\', '/');
}

/// <summary>What EnsureAdminOnly found.</summary>
public enum FolderSecureResult
{
    /// <summary>Already admin-only.</summary>
    AlreadySecure,

    /// <summary>Didn't exist; created admin-only.</summary>
    Created,

    /// <summary>
    /// Existed but others could change it, so it was locked down. Anything
    /// already in it may have been changed by someone else and shouldn't be
    /// trusted (re-create it rather than reuse it).
    /// </summary>
    Tightened
}

/// <summary>Creates and checks admin-only folders (see FolderAclPolicy for why).</summary>
public static class SecureFolders
{
    /// <summary>
    /// Makes sure <paramref name="path"/> exists and only administrators and
    /// SYSTEM can change it. Creates it locked down if missing. If it exists
    /// and an administrator owns it, any broad write access is removed. If a
    /// non-administrator owns it and it's empty, it's taken over and locked
    /// down. If a non-administrator owns it and it has contents (possibly
    /// seeded), or it's a link to somewhere else, it throws instead.
    /// </summary>
    public static FolderSecureResult EnsureAdminOnly(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists)
        {
            string? parent = Path.GetDirectoryName(path);
            if (parent is not null && !Directory.Exists(parent)) EnsureAdminOnly(parent);
            dir.Create(AdminOnlySecurity());
            return FolderSecureResult.Created;
        }

        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new SecurityException($"{path} is a link (junction or symbolic link), not a real folder. Remove it and run the toolkit again.");

        var security = dir.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        string? owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        if (!FolderAclPolicy.IsTrustedOwner(owner))
        {
            // An empty folder can't have been seeded with anything: take it
            // over and lock it down. One with contents is left alone for an
            // administrator to look at.
            if (!dir.EnumerateFileSystemInfos().Any())
            {
                dir.SetAccessControl(AdminOnlySecurity());
                // Checked again once locked, in case something was added in between.
                if (!dir.EnumerateFileSystemInfos().Any()) return FolderSecureResult.Tightened;
            }
            throw new SecurityException(
                $"{path} already exists and is owned by {Describe(owner)}, not by administrators. " +
                "A non-administrator may have created it to plant files. Check what's in it, delete it, and run the toolkit again.");
        }

        if (FolderAclPolicy.UntrustedWriters(ReadRules(security)).Count > 0)
        {
            dir.SetAccessControl(AdminOnlySecurity());
            return FolderSecureResult.Tightened;
        }
        return FolderSecureResult.AlreadySecure;
    }

    /// <summary>Broad groups that can change this folder (empty when it's properly locked down).</summary>
    public static IReadOnlyList<string> UntrustedWriters(string path)
    {
        try
        {
            var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
            return FolderAclPolicy.UntrustedWriters(ReadRules(security));
        }
        catch
        {
            return Array.Empty<string>(); // can't read the ACL: say nothing rather than guess
        }
    }

    private static IEnumerable<AclRule> ReadRules(DirectorySecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => new AclRule(
                ((SecurityIdentifier)r.IdentityReference).Value,
                (int)r.FileSystemRights,
                r.AccessControlType == AccessControlType.Allow,
                r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)));

    private static DirectorySecurity AdminOnlySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(admins);
        return security;
    }

    private static string Describe(string? sid)
    {
        if (sid is null) return "an unknown account";
        try { return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
        catch { return sid; }
    }
}
