using System.Text.RegularExpressions;

namespace GraniteNiFiDeploy.Core;

/// <summary>
/// The edits the Granite install guide makes to NiFi's files by hand
/// (https://granitewms.github.io/GraniteDocs/7.0/tools/nifi/), as pure text
/// functions so the harness checks them against the real 2.x files.
/// </summary>
public static class NiFiConfigFiles
{
    /// <summary>bin\nifi-env.cmd pointing NiFi at the JDK bundled in its own jdk folder.</summary>
    public const string NiFiEnvCmd =
        "@echo off\r\n" +
        "rem Written by Granite NiFi Deploy (GraniteWMS Toolkit).\r\n" +
        "\r\n" +
        "rem Set application home directory based on parent directory of script location\r\n" +
        "pushd %~dp0..\r\n" +
        "set NIFI_HOME=%CD%\r\n" +
        "popd\r\n" +
        "\r\n" +
        "rem Java bundled with this install\r\n" +
        "set JAVA_HOME=%NIFI_HOME%\\jdk\r\n" +
        "\r\n" +
        "rem Set run directory for process identifier tracking\r\n" +
        "set NIFI_PID_DIR=%NIFI_HOME%\\run\r\n" +
        "\r\n" +
        "rem Set application log directory\r\n" +
        "set NIFI_LOG_DIR=%NIFI_HOME%\\logs\r\n";

    private const string StartMinimized = "call start /MIN \"Apache NiFi\" \"%JAVA_EXE%\"";
    private const string StartAttached = "call \"%JAVA_EXE%\"";

    public enum CmdPatch { Patched, AlreadyPatched, NotFound }

    /// <summary>
    /// Removes <c>start /MIN</c> from the start branch of bin\nifi.cmd so
    /// NSSM stays attached to Java instead of seeing the script exit and
    /// restarting it in a loop (the guide's "Configure nifi.cmd" step).
    /// </summary>
    public static (string Text, CmdPatch Result) PatchNiFiCmd(string text)
    {
        if (text.Contains(StartMinimized, StringComparison.Ordinal))
            return (text.Replace(StartMinimized, StartAttached, StringComparison.Ordinal), CmdPatch.Patched);

        // Already attached: the start branch calls Java directly.
        var startBranch = Regex.Match(text, @"RUN_COMMAND%\s*==\s*""start""\s*\(\s*(?:rem[^\n]*\n\s*)*(?<line>[^\n]*)", RegexOptions.IgnoreCase);
        if (startBranch.Success && startBranch.Groups["line"].Value.TrimStart().StartsWith(StartAttached, StringComparison.Ordinal))
            return (text, CmdPatch.AlreadyPatched);

        return (text, CmdPatch.NotFound);
    }

    /// <summary>Value of <c>key=value</c> in a .properties or bootstrap.conf text, or null.</summary>
    public static string? GetProperty(string text, string key)
    {
        var m = Regex.Match(text, "^" + Regex.Escape(key) + "=(.*?)\\r?$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Sets an existing <c>key=value</c> line, keeping the file's line endings.
    /// Throws when the key isn't there (a different NiFi layout should stop
    /// the install, not be silently ignored).
    /// </summary>
    public static string SetProperty(string text, string key, string value)
    {
        if (value.Contains('\n') || value.Contains('\r')) throw new ArgumentException($"{key}: value can't contain a line break.");
        var pattern = new Regex("^" + Regex.Escape(key) + "=.*?(?=\\r?$)", RegexOptions.Multiline);
        if (!pattern.IsMatch(text)) throw new InvalidDataException($"{key} not found. This NiFi version's files aren't laid out as expected.");
        return pattern.Replace(text, _ => key + "=" + value, 1);
    }

    /// <summary>bootstrap.conf with NiFi's heap set (java.arg.2 = -Xms, java.arg.3 = -Xmx).</summary>
    public static string SetHeap(string bootstrapConf, string heap)
    {
        if (!InputRules.IsValidHeap(heap, out string error)) throw new ArgumentException(error);
        string current2 = GetProperty(bootstrapConf, "java.arg.2") ?? "";
        string current3 = GetProperty(bootstrapConf, "java.arg.3") ?? "";
        if (!current2.StartsWith("-Xms") || !current3.StartsWith("-Xmx"))
            throw new InvalidDataException("bootstrap.conf: java.arg.2 and java.arg.3 aren't the heap settings in this NiFi version.");
        string text = SetProperty(bootstrapConf, "java.arg.2", "-Xms" + heap);
        return SetProperty(text, "java.arg.3", "-Xmx" + heap);
    }

    /// <summary>
    /// Java arguments for NiFi's SetSingleUserCredentials tool, the same call
    /// bin\nifi.cmd makes, but passed as separate arguments with no shell in
    /// between, so any character in the password is safe. Run it with the
    /// NiFi home as the working folder (nifi.properties uses ./conf paths).
    /// </summary>
    public static IReadOnlyList<string> CredentialsArguments(string nifiHome, string user, string password, char pathSeparator)
    {
        string conf = Path.Combine(nifiHome, "conf");
        string bootstrapLib = Path.Combine(nifiHome, "lib", "bootstrap");
        return new[]
        {
            "-cp", Path.Combine(bootstrapLib, "*") + pathSeparator + conf,
            "-Dorg.apache.nifi.bootstrap.config.log.dir=" + Path.Combine(nifiHome, "logs"),
            "-Dorg.apache.nifi.bootstrap.config.file=" + Path.Combine(conf, "bootstrap.conf"),
            "-Dnifi.properties.file.path=" + Path.Combine(conf, "nifi.properties"),
            "org.apache.nifi.authentication.single.user.command.SetSingleUserCredentials",
            user,
            password
        };
    }

    /// <summary>
    /// Folders that must exist before the service starts. NSSM opens
    /// AppStdout/AppStderr (logs\service.log) itself and doesn't create the
    /// folder, and the NiFi zip ships no logs folder (NiFi makes it on its
    /// first run). Without it NSSM can't open the file and the service stops
    /// at once: "Unexpected status SERVICE_STOPPED in response to START control"
    /// (first Windows run, 2026-10-04).
    /// </summary>
    public static IReadOnlyList<string> FoldersBeforeService(string nifiHome) => new[]
    {
        Path.Combine(nifiHome, "logs"),
        Path.Combine(nifiHome, "run")
    };

    /// <summary>
    /// NSSM command lines that install NiFi as a service, as the guide does
    /// (application nifi.cmd, argument start, folder bin) plus automatic
    /// start, rotated logs and a minute to shut down cleanly.
    /// </summary>
    public static IReadOnlyList<string[]> NssmInstallCommands(string serviceName, string nifiHome, string version)
    {
        string bin = Path.Combine(nifiHome, "bin");
        string log = Path.Combine(nifiHome, "logs", "service.log");
        return new[]
        {
            new[] { "install", serviceName, Path.Combine(bin, "nifi.cmd"), "start" },
            new[] { "set", serviceName, "AppDirectory", bin },
            new[] { "set", serviceName, "DisplayName", $"Apache NiFi {version}" },
            new[] { "set", serviceName, "Description", "Apache NiFi for GraniteWMS integrations (GraniteWMS Toolkit, NiFi Deploy)" },
            new[] { "set", serviceName, "Start", "SERVICE_AUTO_START" },
            new[] { "set", serviceName, "AppStdout", log },
            new[] { "set", serviceName, "AppStderr", log },
            new[] { "set", serviceName, "AppRotateFiles", "1" },
            new[] { "set", serviceName, "AppRotateBytes", "10485760" },
            new[] { "set", serviceName, "AppStopMethodConsole", "60000" }
        };
    }
}
