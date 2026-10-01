using System.Diagnostics;
using System.Text;

namespace GraniteAttachInstaller.Core;

public sealed record ProcessResult(int ExitCode, string Output)
{
    public bool Succeeded(params int[] okCodes) =>
        okCodes.Length == 0 ? ExitCode == 0 : okCodes.Contains(ExitCode);
}

/// <summary>
/// Runs appcmd, icacls, netsh and dotnet without a console window,
/// capturing their output. Copied from the GraniteWMS Install Wizard's
/// Core/ProcessRunner.cs unchanged apart from the namespace - same job,
/// same external tools.
/// </summary>
public static class ProcessRunner
{
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

        process.WaitForExit();
        lock (output) return new ProcessResult(process.ExitCode, output.ToString());
    }

    public static string Describe(string fileName, IEnumerable<string> arguments) =>
        Path.GetFileName(fileName) + " " + string.Join(" ", arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
}
