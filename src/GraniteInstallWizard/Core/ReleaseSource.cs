using System.IO.Compression;

namespace GraniteInstallWizard.Core;

/// <summary>
/// Turns what Step 1 points at (a release folder or a release .zip) into
/// the release folder the rest of the wizard reads from.
/// </summary>
/// <remarks>
/// v0.4.0 (Izak's request): releases arrive as zips, and extracting one by
/// hand before every install is an easy step to get wrong. A zip is
/// extracted to C:\ProgramData\Granite Install Wizard\Releases\&lt;zip name&gt;.
/// A marker file records what was extracted, so picking the same zip again
/// reuses the extraction. Zips often wrap everything in one top-level folder
/// ("Granite V7.0\..."), so the release root is searched for up to two
/// levels down.
///
/// v0.5.0, for V7.0: V7.0 packs each app as a zip inside the release
/// (GraniteBusinessApi.zip, GraniteDatabase\GraniteDatabase.zip,
/// Hotfix\ProcessApp.zip and so on) instead of a folder. The inner zips the
/// core stack needs (see ReleaseLayout.IsNeededInnerZip) are unpacked next
/// to where they sit, which gives the same layout as V6.0. The rest
/// (Telemetry at 470 MB, integrations, label printing, ERP database packs)
/// are skipped, which also keeps the extraction small. A V7.0 release that
/// was unzipped by hand still has its inner zips, so a folder like that is
/// unpacked into the same Releases folder rather than into the user's own.
/// </remarks>
public static class ReleaseSource
{
    private const string MarkerFile = ".granite-install-wizard-extracted";

    public static bool IsZip(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    public static string ExtractionRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Granite Install Wizard", "Releases");

    /// <summary>
    /// The folder at or under <paramref name="folder"/> (up to two levels
    /// down) that passes ReleaseFolderCheck, or null. The folder itself wins,
    /// then the shallowest match; two equally deep matches are ambiguous and
    /// return null rather than guessing.
    /// </summary>
    public static string? FindReleaseRoot(string folder) =>
        FindShallowest(folder, d => ReleaseFolderCheck.Problems(d).Count == 0);

    /// <summary>
    /// The folder at or under <paramref name="folder"/> that holds a release
    /// packed as inner zips (V7.0 style), or null.
    /// </summary>
    public static string? FindPackedReleaseRoot(string folder) =>
        FindShallowest(folder, IsPackedRelease);

    /// <summary>
    /// True when the folder has every core app, either as a folder or as a
    /// zip, and at least one of them is a zip, plus the database (as
    /// GraniteDatabase\GraniteDatabase or GraniteDatabase\GraniteDatabase.zip).
    /// </summary>
    public static bool IsPackedRelease(string folder)
    {
        if (!Directory.Exists(folder)) return false;
        string? db = ReleaseLayout.FindDirectory(folder, "GraniteDatabase");
        if (db is null) return false;

        var parts = Models.GraniteComponent.CoreStack.Select(c => (dir: folder, name: c.ReleaseFolder))
            .Append((dir: folder, name: "GraniteScaffold"))
            .Append((dir: db, name: "GraniteDatabase"));
        bool anyZip = false;
        foreach (var (dir, name) in parts)
        {
            if (ReleaseLayout.FindDirectory(dir, name) is not null) continue;
            if (FindZip(dir, name) is null) return false;
            anyZip = true;
        }
        return anyZip;
    }

    private static string? FindZip(string dir, string stem)
    {
        try
        {
            return Directory.GetFiles(dir, "*.zip").FirstOrDefault(f => ReleaseLayout.SameName(Path.GetFileNameWithoutExtension(f), stem));
        }
        catch { return null; }
    }

    private static string? FindShallowest(string folder, Func<string, bool> match)
    {
        if (!Directory.Exists(folder)) return null;
        if (match(folder)) return folder;

        var level = new List<string> { folder };
        for (int depth = 1; depth <= 2; depth++)
        {
            level = level.SelectMany(SafeSubdirectories).ToList();
            var matches = level.Where(match).ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1) return null;
        }
        return null;
    }

    private static IEnumerable<string> SafeSubdirectories(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Extracts the zip (or reuses an earlier extraction of the same file),
    /// unpacks the inner zips the core stack needs, and returns the release
    /// folder.
    /// </summary>
    /// <param name="zipPath">The release .zip.</param>
    /// <param name="extractionRoot">Where extractions live; ExtractionRoot in the app, a temp folder in the harness.</param>
    /// <param name="progress">Percent complete, 0..100.</param>
    /// <exception cref="InvalidDataException">The zip has an unsafe entry or no Granite release inside it.</exception>
    public static string ExtractZip(string zipPath, string extractionRoot, IProgress<int>? progress, CancellationToken token)
    {
        var info = new FileInfo(zipPath);
        string stamp = $"zip|{info.Length}|{info.LastWriteTimeUtc:O}|{info.FullName}";
        string target = Path.Combine(extractionRoot, Path.GetFileNameWithoutExtension(zipPath));

        if (TryReuse(target, stamp, out string? reused))
        {
            progress?.Report(100);
            return reused!;
        }
        ResetFolder(target);

        using (ZipArchive zip = ZipFile.OpenRead(zipPath))
        {
            // Inner zips nobody needs are never written out.
            var entries = zip.Entries.Where(e => !IsZipEntry(e) || ReleaseLayout.IsNeededInnerZip(e.Name)).ToList();
            ExtractEntries(entries, target, new Scaled(progress, 0, 50), token);
        }
        ExpandInnerZips(target, deleteAfter: true, new Scaled(progress, 50, 100), token);

        return Finish(target, stamp, "The zip doesn't contain a Granite release (no folder with GraniteBusinessAPI, GraniteDatabase, GraniteScaffold\\Prerequisites and the other release folders).");
    }

    /// <summary>
    /// Unpacks a release folder whose apps are still inner zips (a V7.0 zip
    /// that was unzipped by hand) into <paramref name="extractionRoot"/>,
    /// leaving the original folder untouched. Reused while nothing in the
    /// folder changes.
    /// </summary>
    public static string PreparePackedFolder(string folder, string extractionRoot, IProgress<int>? progress, CancellationToken token)
    {
        string source = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || ReleaseLayout.IsNeededInnerZip(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        long totalBytes = 0, newest = 0;
        foreach (string f in files)
        {
            var fi = new FileInfo(f);
            totalBytes += fi.Length;
            newest = Math.Max(newest, fi.LastWriteTimeUtc.Ticks);
        }
        string stamp = $"folder|{files.Count}|{totalBytes}|{newest}|{source}";
        string target = Path.Combine(extractionRoot, Path.GetFileName(source));

        if (TryReuse(target, stamp, out string? reused))
        {
            progress?.Report(100);
            return reused!;
        }
        ResetFolder(target);
        string targetFull = Path.GetFullPath(target) + Path.DirectorySeparatorChar;

        // Loose files are copied (they're small: Hotfix binaries, SQL, the
        // odd config); the needed inner zips are unpacked straight from the
        // user's folder into the copy.
        var copyProgress = new Scaled(progress, 0, 30);
        var zips = new List<string>();
        for (int i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            string rel = Path.GetRelativePath(source, files[i]);
            if (files[i].EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { zips.Add(rel); continue; }
            string destination = Path.GetFullPath(Path.Combine(target, rel));
            if (!destination.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(files[i], destination, overwrite: true);
            copyProgress.Report(files.Count == 0 ? 100 : (i + 1) * 100 / files.Count);
        }
        copyProgress.Report(100);

        var unpackProgress = new Scaled(progress, 30, 100);
        for (int i = 0; i < zips.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            string destDir = Path.GetDirectoryName(Path.Combine(target, zips[i]))!;
            Directory.CreateDirectory(destDir);
            ExpandOne(Path.Combine(source, zips[i]), destDir, token);
            unpackProgress.Report((i + 1) * 100 / zips.Count);
        }
        unpackProgress.Report(100);

        return Finish(target, stamp, "The folder has inner zips, but no complete Granite release came out of them.");
    }

    private static bool IsZipEntry(ZipArchiveEntry e) =>
        e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static bool TryReuse(string target, string stamp, out string? release)
    {
        release = null;
        string marker = Path.Combine(target, MarkerFile);
        if (!File.Exists(marker) || File.ReadAllText(marker) != stamp) return false;
        release = FindReleaseRoot(target);
        return release is not null;
    }

    private static void ResetFolder(string target)
    {
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
    }

    private static string Finish(string target, string stamp, string notFoundMessage)
    {
        string? root = FindReleaseRoot(target);
        if (root is null) throw new InvalidDataException(notFoundMessage);
        File.WriteAllText(Path.Combine(target, MarkerFile), stamp);
        return root;
    }

    /// <summary>Unpacks every needed inner zip under <paramref name="root"/> next to where it sits.</summary>
    private static void ExpandInnerZips(string root, bool deleteAfter, IProgress<int> progress, CancellationToken token)
    {
        var zips = Directory.GetFiles(root, "*.zip", SearchOption.AllDirectories)
            .Where(z => ReleaseLayout.IsNeededInnerZip(Path.GetFileName(z)))
            .OrderBy(z => z, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (int i = 0; i < zips.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            ExpandOne(zips[i], Path.GetDirectoryName(zips[i])!, token);
            if (deleteAfter) File.Delete(zips[i]);
            progress.Report((i + 1) * 100 / zips.Count);
        }
        progress.Report(100);
    }

    /// <summary>
    /// Unpacks one inner zip into <paramref name="destDir"/>. V7.0's inner
    /// zips carry their own top folder (GraniteBusinessApi.zip holds
    /// GraniteBusinessApi\...), so they're unpacked as they are. One without
    /// a single top folder gets a folder named after the zip, so its files
    /// don't spill into the release root.
    /// </summary>
    private static void ExpandOne(string zipPath, string destDir, CancellationToken token)
    {
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        var tops = zip.Entries
            .Select(EntryPath)
            .OfType<string>()
            .Select(n => n.Contains('/') ? n[..n.IndexOf('/')] : "\0file")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        string into = tops.Count == 1 && tops[0] != "\0file"
            ? destDir
            : Path.Combine(destDir, Path.GetFileNameWithoutExtension(zipPath));
        ExtractEntries(zip.Entries.ToList(), into, null, token);
    }

    /// <summary>
    /// The entry's path relative to the extraction folder, with forward
    /// slashes, or null for an entry that is only the zip's root.
    /// </summary>
    /// <remarks>
    /// v0.5.1: Granite V6.0.zip has an entry named just "/" (the root folder
    /// its zip tool stored). Path.Combine treats "/" as rooted, so it resolved
    /// to C:\ and the zip-slip check refused the whole zip. Leading slashes are
    /// now stripped (every zip tool treats entry names as relative), and an
    /// empty name is skipped. A drive letter ("C:...") is never relative, so it
    /// is left in place for the zip-slip check to refuse.
    /// </remarks>
    internal static string? EntryPath(ZipArchiveEntry entry)
    {
        string name = entry.FullName.Replace('\\', '/').TrimStart('/');
        return name.Length == 0 ? null : name;
    }

    /// <summary>Extracts entries under <paramref name="target"/>, refusing any that would land outside it.</summary>
    private static void ExtractEntries(IReadOnlyList<ZipArchiveEntry> entries, string target, IProgress<int>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(target);
        string targetFull = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        int total = entries.Count, done = 0, lastReported = -1;
        foreach (ZipArchiveEntry entry in entries)
        {
            token.ThrowIfCancellationRequested();
            string? name = EntryPath(entry);
            if (name is null)
            {
                done++;
                continue; // the zip's own root ("/"), nothing to extract
            }
            string destination = Path.GetFullPath(Path.Combine(target, name));
            // Zip slip: an entry like ..\..\Windows\x.dll must never land outside the target.
            if (!destination.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The zip contains an unsafe path and was not extracted: {entry.FullName}");

            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            done++;
            int pct = total == 0 ? 100 : done * 100 / total;
            if (pct != lastReported) { progress?.Report(pct); lastReported = pct; }
        }
        if (total == 0) progress?.Report(100);
    }

    /// <summary>Maps 0..100 of one phase onto a slice of the overall progress.</summary>
    private sealed class Scaled : IProgress<int>
    {
        private readonly IProgress<int>? _inner;
        private readonly int _from, _to;
        private int _last = -1;
        public Scaled(IProgress<int>? inner, int from, int to) { _inner = inner; _from = from; _to = to; }
        public void Report(int value)
        {
            int scaled = _from + (_to - _from) * Math.Clamp(value, 0, 100) / 100;
            if (scaled == _last) return;
            _last = scaled;
            _inner?.Report(scaled);
        }
    }
}
