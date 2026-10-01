using System.Runtime.InteropServices;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Validates a Windows username/password pair up front, via the same
/// LogonUser Win32 API the OS itself uses to check credentials -- without
/// actually doing anything with the identity (no profile is loaded, the
/// token is closed immediately).
/// </summary>
/// <remarks>
/// Added after a real run showed exactly the failure mode this exists to
/// prevent: Panel 5 accepted a Windows account/password with no checking,
/// the wizard then ran the entire SQL deployment (dozens of batches, tens
/// of seconds), and only at the very end did Task Scheduler registration
/// fail with "The user name or password is incorrect. (0x8007052E)" --
/// by which point re-running meant sitting through the whole deployment
/// again. Checking this on Panel 5, before Start Deployment is even
/// reachable, catches a bad password in under a second instead.
/// </remarks>
public static class WindowsCredentialValidator
{
    private const int LOGON32_LOGON_NETWORK = 3;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(
        string lpszUsername, string? lpszDomain, string lpszPassword,
        int dwLogonType, int dwLogonProvider, out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public sealed record ValidationResult(bool Success, string Message);

    /// <summary>
    /// Accepts the same account name shapes the rest of the wizard does:
    /// "DOMAIN\user", ".\user" (explicitly local), "user@domain.com" (UPN),
    /// or a bare "user" (treated as local, same as ".\user" -- this is the
    /// shape that actually broke on the real run this was built for).
    /// </summary>
    public static ValidationResult Validate(string accountName, string password)
    {
        string? domain;
        string user;

        int separator = accountName.IndexOf('\\');
        if (separator >= 0)
        {
            domain = accountName[..separator];
            user = accountName[(separator + 1)..];
        }
        else if (accountName.Contains('@'))
        {
            domain = null; // UPN form -- LogonUser resolves the domain itself
            user = accountName;
        }
        else
        {
            domain = "."; // bare name -- treat as a local account, same as ".\name"
            user = accountName;
        }

        bool success = LogonUser(user, domain, password, LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_DEFAULT, out IntPtr token);
        if (success)
        {
            CloseHandle(token);
            return new ValidationResult(true, "Account and password verified.");
        }

        int error = Marshal.GetLastWin32Error();
        string message = error switch
        {
            1326 => "The account name or password is incorrect.",
            1330 => "This account's password has expired and must be changed before it can be used.",
            1327 => "This account has a logon restriction (hours/workstation) that blocked verification -- it may still work when Task Scheduler actually runs it.",
            1331 => "This account is currently disabled.",
            1793 => "This account has expired.",
            2 => "That account could not be found -- check the domain/computer name and spelling.",
            1385 => "This account isn't allowed to log on this way here -- check with whoever manages this machine/domain.",
            1789 => "This computer could not reach a domain controller to verify a domain account -- check connectivity, or verify it manually.",
            _ => $"Windows rejected these credentials (error {error})."
        };
        return new ValidationResult(false, message);
    }
}
