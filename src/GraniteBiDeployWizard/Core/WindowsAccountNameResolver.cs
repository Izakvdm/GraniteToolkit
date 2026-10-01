namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Expands the ".\" "this computer" shorthand -- which LogonUser and the
/// wizard's own account fields accept natively -- into the literal computer
/// name, for the consumers that don't accept the shorthand themselves.
/// </summary>
/// <remarks>
/// Two real failures on the same account string (".\izakm") led here:
/// <list type="bullet">
/// <item>SQL Server's <c>CREATE LOGIN ... FROM WINDOWS</c> fails with
/// "Windows NT user or group '.\izakm' not found" -- it needs the actual
/// computer name (see <c>BootstrapLoginService</c>).</item>
/// <item>The Task Scheduler COM API's task definition XML rejects a ".\"
/// prefixed UserId at schema-validation time, surfacing as a cryptic
/// "(21,8):UserId:" exception rather than a readable message (see
/// <c>ScheduledTaskService</c>).</item>
/// </list>
/// Only correct when this wizard runs on the same machine as both the SQL
/// Server instance and the account being registered -- the common case
/// this feature targets. For a genuinely remote server, a ".\name" account
/// wouldn't identify the right machine either way; type the real
/// "COMPUTERNAME\name" form instead of relying on the "." shorthand.
/// </remarks>
public static class WindowsAccountNameResolver
{
    public static string ResolveLocalShorthand(string windowsAccountName) =>
        windowsAccountName.StartsWith(@".\", StringComparison.Ordinal)
            ? Environment.MachineName + windowsAccountName[1..]
            : windowsAccountName;
}
