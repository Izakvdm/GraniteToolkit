using System.Diagnostics;
using System.Text;

namespace GraniteInstallWizard.Core;

public sealed record ProcessResult(int ExitCode, string Output)
{
    public bool Succeeded(params int[] okCodes) =>
        okCodes.Length == 0 ? ExitCode == 0 : okCodes.Contains(ExitCode);
}

/// <summary>
/// Runs dism, appcmd, netsh, icacls, msiexec and the Hosting Bundle
/// installers without a console window, capturing their output.
/// </summary>
public static class ProcessRunner
{
    /// <param name="fileName">Executable path or name on PATH.</param>
    /// <param name="arguments">One entry per argument; quoting is done by ProcessStartInfo.</param>
    /// <param name="timeout">Kills the process if it runs longer. Installers get generous timeouts.</param>
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken token)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string a in arguments) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish within {timeout.TotalMinutes:0} minutes.");
        }

        // WaitForExitAsync returns before the async output readers drain.
        process.WaitForExit();
        lock (output) return new ProcessResult(process.ExitCode, output.ToString());
    }

    /// <summary>Command line as it would be typed, for the log.</summary>
    public static string Describe(string fileName, IEnumerable<string> arguments) =>
        Path.GetFileName(fileName) + " " + string.Join(" ", arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
}
