namespace Granite.Toolkit.Core.Security;

public enum SignatureState
{
    /// <summary>No Authenticode signature at all (a development build).</summary>
    Unsigned,

    /// <summary>Signed, the file is unchanged since signing, and the chain is trusted.</summary>
    Valid,

    /// <summary>Signed, but the file was changed afterwards or the chain isn't trusted.</summary>
    Invalid
}

/// <summary>The result of checking one file's Authenticode signature.</summary>
public sealed record SignatureInfo(SignatureState State, string? SignerSubject, string Detail)
{
    public static SignatureInfo Unsigned(string detail = "Not signed") => new(SignatureState.Unsigned, null, detail);
}

public enum LaunchDecision
{
    Allow,

    /// <summary>Allowed, but the operator is told it's a development (unsigned) build.</summary>
    AllowDevelopmentBuild,

    Block
}

public sealed record LaunchVerdict(LaunchDecision Decision, string Reason)
{
    public bool CanLaunch => Decision != LaunchDecision.Block;
}

/// <summary>
/// Decides whether the launcher may start a module exe, from the two
/// signatures. Pure, so the harness checks every combination.
/// </summary>
/// <remarks>
/// <para>
/// A signed launcher only starts modules signed by the same publisher. The
/// comparison is on the signer's subject (the validated publisher name),
/// not the thumbprint: Azure Artifact Signing issues a new certificate
/// every day, so two files from the same release can carry different
/// thumbprints while the subject stays the same.
/// </para>
/// <para>
/// An unsigned launcher is a development build. It may start unsigned or
/// signed modules, with a visible warning, but never a module whose
/// signature is broken: that means the file was changed after signing.
/// A launcher whose own signature is broken starts nothing.
/// </para>
/// </remarks>
public static class SignaturePolicy
{
    public static LaunchVerdict Decide(SignatureInfo launcher, SignatureInfo module)
    {
        if (launcher.State == SignatureState.Invalid)
            return new(LaunchDecision.Block, $"The toolkit's own signature doesn't verify ({launcher.Detail}). Reinstall it from a trusted copy.");

        if (module.State == SignatureState.Invalid)
            return new(LaunchDecision.Block, $"This module's signature doesn't verify ({module.Detail}). The file may have been changed; reinstall the toolkit.");

        if (launcher.State == SignatureState.Unsigned)
            return new(LaunchDecision.AllowDevelopmentBuild, "Development build: the toolkit isn't signed, so modules can't be checked against a publisher.");

        // Launcher is validly signed from here on.
        if (module.State == SignatureState.Unsigned)
            return new(LaunchDecision.Block, "This module isn't signed, but the toolkit is. A signed release never ships unsigned modules, so it was probably replaced. Reinstall the toolkit.");

        if (!SamePublisher(launcher.SignerSubject, module.SignerSubject))
            return new(LaunchDecision.Block, $"This module is signed by \"{module.SignerSubject}\", not by the toolkit's publisher \"{launcher.SignerSubject}\".");

        return new(LaunchDecision.Allow, $"Signed by {module.SignerSubject}");
    }

    public static bool SamePublisher(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    /// <summary>"CN=A, O=B" and "CN=A,O=B" are the same name.</summary>
    private static string Normalize(string subject) =>
        string.Join(",", subject.Split(',').Select(p => p.Trim()));
}
