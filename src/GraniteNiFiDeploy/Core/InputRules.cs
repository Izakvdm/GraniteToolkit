using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace GraniteNiFiDeploy.Core;

/// <summary>Checks on what the operator types, shared by the wizard steps and the harness.</summary>
public static class InputRules
{
    public const int MinNiFiPassword = 12;   // NiFi's single-user provider refuses shorter
    public const int MaxNiFiPassword = 128;

    public static bool IsValidNiFiUser(string user, out string error)
    {
        error = Regex.IsMatch(user, @"^[A-Za-z0-9._@-]{3,64}$")
            ? string.Empty
            : "The NiFi user name needs 3 to 64 letters, digits or . _ @ -";
        return error.Length == 0;
    }

    public static bool IsValidNiFiPassword(string password, string confirm, out string error)
    {
        if (password.Length < MinNiFiPassword) error = $"The NiFi password needs at least {MinNiFiPassword} characters.";
        else if (password.Length > MaxNiFiPassword) error = $"The NiFi password can be at most {MaxNiFiPassword} characters.";
        else if (password != password.Trim()) error = "The NiFi password can't start or end with a space.";
        else if (password != confirm) error = "The two NiFi passwords don't match.";
        else error = string.Empty;
        return error.Length == 0;
    }

    public static bool IsValidServiceName(string name, out string error)
    {
        error = Regex.IsMatch(name, @"^[A-Za-z0-9_.-]{1,80}$")
            ? string.Empty
            : "The service name can only use letters, digits and _ . - (no spaces).";
        return error.Length == 0;
    }

    public static bool IsValidPort(int port, out string error)
    {
        error = port is >= 1024 and <= 65535 ? string.Empty : "Choose a port between 1024 and 65535.";
        return error.Length == 0;
    }

    /// <summary>"1g", "2048m". At least 512 MB, at most 64 GB.</summary>
    public static bool IsValidHeap(string heap, out string error)
    {
        var m = Regex.Match(heap, @"^(\d{1,5})([mMgG])$");
        long mb = !m.Success ? 0 : long.Parse(m.Groups[1].Value) * (m.Groups[2].Value.ToLowerInvariant() == "g" ? 1024 : 1);
        error = mb is >= 512 and <= 65536 ? string.Empty : "Heap is a size like 1g, 2g or 1536m (512m to 64g).";
        return error.Length == 0;
    }

    /// <summary>
    /// A local, absolute folder that isn't a drive root or a Windows system
    /// folder. NiFi and the import folders shouldn't live on a share.
    /// </summary>
    public static bool IsValidLocalFolder(string path, string what, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) { error = $"Enter the {what}."; return false; }
        string p = path.Trim();
        if (p.StartsWith(@"\\")) { error = $"The {what} must be on a local drive, not a network share."; return false; }
        if (!Regex.IsMatch(p, @"^[A-Za-z]:\\[^<>:""|?*]+$")) { error = $"The {what} must be a full path like C:\\nifi."; return false; }
        if (p.Split('\\').Skip(1).Any(part => part.Length == 0 || part.Trim('.').Length == 0 || part != part.TrimEnd(' ', '.')))
        { error = $"The {what} has an empty, dot or trailing-space folder name in it."; return false; }
        string lower = p.TrimEnd('\\').ToLowerInvariant();
        string[] system = { @"\windows", @"\program files", @"\program files (x86)", @"\programdata", @"\users" };
        string afterDrive = lower[2..];
        if (system.Any(s => afterDrive == s || afterDrive.StartsWith(s + @"\")))
        { error = $"Don't put the {what} under a Windows system folder (Windows, Program Files, ProgramData, Users)."; return false; }
        return true;
    }

    /// <summary>Two folders must not be the same or one inside the other.</summary>
    public static bool Overlap(string a, string b)
    {
        string x = a.TrimEnd('\\').ToLowerInvariant() + @"\";
        string y = b.TrimEnd('\\').ToLowerInvariant() + @"\";
        return x.StartsWith(y) || y.StartsWith(x);
    }

    public static bool IsValidSqlLogin(string login, out string error)
    {
        error = Regex.IsMatch(login, @"^[A-Za-z][A-Za-z0-9_.-]{2,63}$")
            ? string.Empty
            : "The NiFi SQL login needs 3 to 64 characters: a letter first, then letters, digits or _ . -";
        return error.Length == 0;
    }

    /// <summary>
    /// A random password for NiFi's SQL login. Nobody types it: it goes
    /// straight into SQL Server and NiFi's sensitive parameter. Every class
    /// of character is present, so SQL Server's password policy accepts it,
    /// and nothing in it needs escaping in a JDBC URL or connection string.
    /// </summary>
    public static string GeneratePassword(int length = 32)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!#%*+-=?@_";
        const string all = upper + lower + digits + symbols;
        if (length < 16) throw new ArgumentOutOfRangeException(nameof(length));

        var chars = new char[length];
        chars[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        chars[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
        for (int i = 4; i < length; i++) chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        // Shuffle so the fixed classes aren't always first.
        for (int i = length - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }
}
