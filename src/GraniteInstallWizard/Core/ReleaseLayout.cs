using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.Core;

/// <summary>
/// Finds folders and files inside a release without caring how they're
/// spelled.
/// </summary>
/// <remarks>
/// Added in v0.5.0 for V7. The same folders are spelled differently between
/// releases: GraniteBusinessAPI (V6.0) vs GraniteBusinessApi (V7.0),
/// GraniteWebdesktop vs GraniteWebDesktop, and Hotfix\BusinessAPI vs
/// Hotfix\Business Api. Windows doesn't care about case, but a space does
/// matter, and the LogicHarness runs on Linux where case matters too. So
/// names are compared ignoring case, spaces, underscores and hyphens.
/// </remarks>
public static class ReleaseLayout
{
    public static string Normalize(string name) =>
        new string(name.Where(ch => ch is not (' ' or '_' or '-')).ToArray()).ToLowerInvariant();

    public static bool SameName(string a, string b) => Normalize(a) == Normalize(b);

    /// <summary>The child directory of <paramref name="parent"/> matching <paramref name="name"/>, or null.</summary>
    public static string? FindDirectory(string parent, string name)
    {
        string exact = Path.Combine(parent, name);
        if (Directory.Exists(exact) && Path.GetFileName(exact) == name) return exact;
        try
        {
            return Directory.GetDirectories(parent).FirstOrDefault(d => SameName(Path.GetFileName(d), name));
        }
        catch { return null; }
    }

    /// <summary>The file in <paramref name="parent"/> matching <paramref name="name"/> ignoring case, or null.</summary>
    public static string? FindFile(string parent, string name)
    {
        string exact = Path.Combine(parent, name);
        if (File.Exists(exact)) return exact;
        try
        {
            return Directory.GetFiles(parent).FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    /// <summary>
    /// Walks <paramref name="parts"/> down from <paramref name="root"/>,
    /// matching each folder loosely and the last part as a file or folder.
    /// When something is missing, returns the plain Path.Combine so error
    /// messages still show the expected path.
    /// </summary>
    public static string Resolve(string root, params string[] parts)
    {
        string current = root;
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            string? next = FindDirectory(current, parts[i]) ?? (last ? FindFile(current, parts[i]) : null);
            if (next is null) return Path.Combine(new[] { root }.Concat(parts).ToArray());
            current = next;
        }
        return current;
    }

    public static bool DirectoryExists(string root, params string[] parts) => Directory.Exists(Resolve(root, parts));

    public static bool FileExists(string root, params string[] parts) => File.Exists(Resolve(root, parts));

    /// <summary>
    /// Zips inside a V7 release that the core stack needs unpacked. Every
    /// other inner zip (Telemetry, Scheduler, integrations, label printing,
    /// the ERP database packs, client configs) is left alone.
    /// </summary>
    public static bool IsNeededInnerZip(string fileName)
    {
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
        string stem = Normalize(Path.GetFileNameWithoutExtension(fileName));
        if (stem is "granitescaffold" or "granitedatabase") return true;
        foreach (var comp in GraniteComponent.CoreStack)
        {
            if (stem == Normalize(comp.ReleaseFolder)) return true;
            if (comp.HotfixFolder.Length > 0 && stem == Normalize(comp.HotfixFolder)) return true;
        }
        return false;
    }
}
