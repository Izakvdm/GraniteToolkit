using System.IO.Compression;

namespace GraniteNiFiDeploy.Core;

/// <summary>
/// Finds the four downloads in what the operator picked: a folder that
/// holds them, or one bundle zip that holds them anywhere inside (like the
/// Rapid Deploy media zip). Bundle contents are unpacked into an admin-only
/// folder first, so nothing a non-administrator can change gets installed.
/// </summary>
public static class MediaStaging
{
    public static string DataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Granite NiFi Deploy");

    public static string LogFolder => Path.Combine(DataRoot, "Logs");

    /// <summary>The newest of each kind of download in a folder (not its subfolders).</summary>
    public static IReadOnlyDictionary<MediaKind, MediaFile> ScanFolder(string folder) =>
        MediaCatalog.PickNewest(Directory.EnumerateFiles(folder));

    /// <summary>
    /// Unpacks the downloads inside a bundle zip to
    /// C:\ProgramData\Granite NiFi Deploy\Media\&lt;bundle name&gt; and returns
    /// what it found there. Returns an empty set when the zip holds none.
    /// </summary>
    public static IReadOnlyDictionary<MediaKind, MediaFile> UnpackBundle(string bundleZip, Action<string> progress)
    {
        using var zip = ZipFile.OpenRead(bundleZip);
        var wanted = MediaCatalog.PickNewest(zip.Entries.Where(e => e.Length > 0).Select(e => e.FullName));
        if (wanted.Count == 0) return wanted;

        SecureFolders.EnsureAdminOnly(DataRoot);
        string mediaRoot = Path.Combine(DataRoot, "Media");
        SecureFolders.EnsureAdminOnly(mediaRoot);
        string target = Path.Combine(mediaRoot, SafeName(Path.GetFileNameWithoutExtension(bundleZip)));
        // Never reuse an earlier unpack: start clean every time.
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);

        foreach (var file in wanted.Values)
        {
            var entry = zip.GetEntry(file.Path) ?? zip.Entries.First(e => e.FullName == file.Path);
            string name = Path.GetFileName(entry.FullName.Replace('\\', '/'));
            if (!MediaCatalog.IsSafeFolderName(name)) throw new InvalidDataException($"Unexpected file name in the bundle: {entry.FullName}");
            progress($"Unpacking {name} ({entry.Length / (1024 * 1024)} MB)");
            entry.ExtractToFile(Path.Combine(target, name), overwrite: true);
        }
        return ScanFolder(target);
    }

    private static string SafeName(string name)
    {
        var chars = name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray();
        string s = new string(chars).Trim('.');
        return s.Length == 0 ? "bundle" : s.Length > 60 ? s[..60] : s;
    }
}
