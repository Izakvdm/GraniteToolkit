using System.IO.Compression;
using System.Text.RegularExpressions;

namespace GraniteNiFiDeploy.Core;

public enum MediaKind { NiFi, Jdk, Nssm, JdbcDriver }

/// <summary>One of the four downloads, found in the media folder.</summary>
/// <param name="Path">Full path to the zip (or, for the JDBC driver, possibly a bare jar).</param>
/// <param name="Version">Version from the file name, for display and picking the newest.</param>
public sealed record MediaFile(MediaKind Kind, string Path, string? Version)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>The four downloads after checking inside them.</summary>
public sealed record MediaSet(
    MediaFile NiFi, string NiFiFolderName, string NiFiVersion,
    MediaFile Jdk, int JavaMajor, string JavaVersion,
    MediaFile Nssm, string NssmEntry,
    MediaFile Jdbc, string JdbcJarName)
{
    public bool JavaIsLts => MediaCatalog.IsLtsJava(JavaMajor);
}

/// <summary>
/// Recognises the NiFi, JDK, NSSM and SQL Server JDBC downloads by name and
/// checks inside each zip without extracting it. Pure (System.IO.Compression
/// only), so the harness runs it against real zips anywhere.
/// </summary>
public static class MediaCatalog
{
    public const int MinimumJava = 21;

    private static readonly Regex NiFiName = new(@"^nifi-(\d+\.\d+\.\d+)-bin\.zip$", RegexOptions.IgnoreCase);
    private static readonly Regex JdkName = new(@"(?:^|[-_])jdk(?:[-_]?(\d+)|.*?[-_](\d+)(?:[._]\d+)*)", RegexOptions.IgnoreCase);
    private static readonly Regex NssmName = new(@"^nssm[-_.]?(\d+(?:\.\d+)*)?.*\.zip$", RegexOptions.IgnoreCase);
    private static readonly Regex JdbcZipName = new(@"^sqljdbc[_-]?(\d+(?:\.\d+)*)?.*\.zip$", RegexOptions.IgnoreCase);
    private static readonly Regex JdbcJarName = new(@"^mssql-jdbc-(\d+(?:\.\d+)*)\.jre(\d+)\.jar$", RegexOptions.IgnoreCase);

    /// <summary>What a download is from its file name alone, or null.</summary>
    public static MediaFile? Classify(string path)
    {
        // Either separator: zip entry names can use / or \ whatever the OS.
        string name = path[(path.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
        Match m;
        if ((m = NiFiName.Match(name)).Success) return new(MediaKind.NiFi, path, m.Groups[1].Value);
        if ((m = JdbcJarName.Match(name)).Success) return new(MediaKind.JdbcDriver, path, m.Groups[1].Value);
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
        if ((m = NssmName.Match(name)).Success) return new(MediaKind.Nssm, path, NullIfEmpty(m.Groups[1].Value));
        if ((m = JdbcZipName.Match(name)).Success) return new(MediaKind.JdbcDriver, path, NullIfEmpty(m.Groups[1].Value));
        if (name.StartsWith("nifi", StringComparison.OrdinalIgnoreCase)) return null; // nifi-toolkit etc.
        if ((m = JdkName.Match(name)).Success)
        {
            string v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            return new(MediaKind.Jdk, path, NullIfEmpty(v));
        }
        return null;
    }

    /// <summary>The newest file of each kind among <paramref name="paths"/>.</summary>
    public static IReadOnlyDictionary<MediaKind, MediaFile> PickNewest(IEnumerable<string> paths)
    {
        var best = new Dictionary<MediaKind, MediaFile>();
        foreach (var file in paths.Select(Classify).OfType<MediaFile>())
        {
            if (!best.TryGetValue(file.Kind, out var current) || CompareVersions(file.Version, current.Version) > 0)
                best[file.Kind] = file;
        }
        return best;
    }

    public static IReadOnlyList<MediaKind> Missing(IReadOnlyDictionary<MediaKind, MediaFile> found) =>
        Enum.GetValues<MediaKind>().Where(k => !found.ContainsKey(k)).ToList();

    public static string Describe(MediaKind kind) => kind switch
    {
        MediaKind.NiFi => "Apache NiFi (nifi-2.x.x-bin.zip)",
        MediaKind.Jdk => "Java JDK for Windows x64 (a jdk zip, version 21 or newer)",
        MediaKind.Nssm => "NSSM (nssm-2.24.zip or similar)",
        MediaKind.JdbcDriver => "Microsoft JDBC Driver for SQL Server (sqljdbc_*.zip or mssql-jdbc-*.jre11.jar)",
        _ => kind.ToString()
    };

    /// <summary>
    /// Opens each zip and checks it is what its name says. Returns the set,
    /// or throws <see cref="InvalidDataException"/> with a readable reason.
    /// </summary>
    public static MediaSet Inspect(IReadOnlyDictionary<MediaKind, MediaFile> found)
    {
        var missing = Missing(found);
        if (missing.Count > 0)
            throw new InvalidDataException("Missing: " + string.Join("; ", missing.Select(Describe)));

        var nifi = found[MediaKind.NiFi];
        string nifiFolder;
        using (var zip = ZipFile.OpenRead(nifi.Path))
            nifiFolder = NiFiTopFolder(zip.Entries.Select(e => e.FullName))
                ?? throw new InvalidDataException($"{nifi.FileName} doesn't look like a NiFi download (no bin/nifi.cmd under one top folder).");
        string nifiVersion = nifi.Version!;
        if (!nifiVersion.StartsWith("2.")) throw new InvalidDataException($"NiFi {nifiVersion} isn't supported. This module and the Granite flow are built for NiFi 2.x.");

        var jdk = found[MediaKind.Jdk];
        string javaVersion;
        using (var zip = ZipFile.OpenRead(jdk.Path))
        {
            var release = zip.Entries.FirstOrDefault(e => IsTopLevelFile(e.FullName, "release"))
                ?? throw new InvalidDataException($"{jdk.FileName} doesn't look like a JDK (no release file under its top folder).");
            if (!zip.Entries.Any(e => IsUnderTop(e.FullName, "bin/java.exe")))
                throw new InvalidDataException($"{jdk.FileName} has no bin\\java.exe. Download the Windows x64 zip, not the Linux or macOS one.");
            using var reader = new StreamReader(release.Open());
            javaVersion = ParseJavaVersion(reader.ReadToEnd())
                ?? throw new InvalidDataException($"Couldn't read JAVA_VERSION from the release file in {jdk.FileName}.");
        }
        int javaMajor = JavaMajor(javaVersion);
        if (javaMajor < MinimumJava) throw new InvalidDataException($"Java {javaVersion} is too old. NiFi 2 needs Java {MinimumJava} or newer.");

        var nssm = found[MediaKind.Nssm];
        string nssmEntry;
        using (var zip = ZipFile.OpenRead(nssm.Path))
            nssmEntry = zip.Entries.Select(e => e.FullName).FirstOrDefault(n => n.Replace('\\', '/').EndsWith("win64/nssm.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"{nssm.FileName} has no win64\\nssm.exe.");

        var jdbc = found[MediaKind.JdbcDriver];
        string jarName;
        if (jdbc.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            jarName = jdbc.FileName;
        }
        else
        {
            using var zip = ZipFile.OpenRead(jdbc.Path);
            jarName = PickJdbcJar(zip.Entries.Select(e => e.FullName), javaMajor)
                ?? throw new InvalidDataException($"{jdbc.FileName} has no mssql-jdbc-*.jre*.jar this Java can use.");
        }

        return new MediaSet(nifi, nifiFolder, nifiVersion, jdk, javaMajor, javaVersion, nssm, nssmEntry, jdbc, jarName);
    }

    /// <summary>The single top folder holding bin/nifi.cmd ("nifi-2.11.0"), or null.</summary>
    public static string? NiFiTopFolder(IEnumerable<string> entryNames)
    {
        foreach (string raw in entryNames)
        {
            string n = raw.Replace('\\', '/');
            var parts = n.Split('/');
            if (parts.Length == 3 && parts[1].Equals("bin", StringComparison.OrdinalIgnoreCase)
                && parts[2].Equals("nifi.cmd", StringComparison.OrdinalIgnoreCase) && IsSafeFolderName(parts[0]))
                return parts[0];
        }
        return null;
    }

    /// <summary>"21.0.5" from a JDK release file's JAVA_VERSION="21.0.5" line.</summary>
    public static string? ParseJavaVersion(string releaseText)
    {
        var m = Regex.Match(releaseText, "^JAVA_VERSION=\"?([0-9][0-9A-Za-z.+_-]*)\"?\\s*$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>21 from "21.0.5", 8 from "1.8.0_402".</summary>
    public static int JavaMajor(string version)
    {
        var parts = version.Split('.', '_', '+', '-');
        int first = int.TryParse(parts[0], out int a) ? a : 0;
        if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], out int b)) return b;
        return first;
    }

    /// <summary>Java LTS releases (21 and every two years after: 25, 29, ...).</summary>
    public static bool IsLtsJava(int major) => major == 17 || (major >= 21 && (major - 21) % 4 == 0);

    /// <summary>
    /// The driver jar to use: the highest jreNN not newer than the Java in
    /// use, skipping the sources and javadoc jars. Null when none fits.
    /// </summary>
    public static string? PickJdbcJar(IEnumerable<string> entryNames, int javaMajor)
    {
        return entryNames
            .Select(n => System.IO.Path.GetFileName(n.Replace('\\', '/')))
            .Select(n => (Name: n, M: JdbcJarName.Match(n)))
            .Where(x => x.M.Success && int.Parse(x.M.Groups[2].Value) <= javaMajor)
            .OrderByDescending(x => CompareKey(x.M.Groups[1].Value))
            .ThenByDescending(x => int.Parse(x.M.Groups[2].Value))
            .Select(x => x.Name)
            .FirstOrDefault();
    }

    /// <summary>Orders "13.4.0" against "9.2" numerically; nulls sort first.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        if (a is null || b is null) return a is null ? (b is null ? 0 : -1) : 1;
        return string.CompareOrdinal(CompareKey(a), CompareKey(b));
    }

    private static string CompareKey(string version) =>
        string.Join(".", version.Split('.').Select(p => int.TryParse(p, out int n) ? n.ToString("D6") : p));

    private static bool IsTopLevelFile(string entry, string file)
    {
        var parts = entry.Replace('\\', '/').Split('/');
        return parts.Length == 2 && parts[1].Equals(file, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderTop(string entry, string relative)
    {
        string n = entry.Replace('\\', '/');
        int slash = n.IndexOf('/');
        return slash > 0 && n[(slash + 1)..].Equals(relative, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A plain folder name: no dots-only, no separators, nothing Windows rejects.</summary>
    public static bool IsSafeFolderName(string name) =>
        name.Length > 0 && name.Trim('.').Length > 0 && name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0
        && name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) < 0;

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
