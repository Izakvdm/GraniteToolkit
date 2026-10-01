using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GraniteDbSwitcher.Core;

/// <summary>A connection string found in an appsettings.json, with where it sits in the file.</summary>
public sealed record FoundConnection(string Name, string Value, long ByteOffset, int ByteLength);

/// <summary>
/// Reads and replaces ConnectionStrings entries in an appsettings.json
/// without re-serialising the file.
/// </summary>
/// <remarks>
/// The install wizard's AppSettingsWriter rewrites the whole document,
/// which drops the // comments the release files carry. That's fine once
/// at install time, but the switcher edits the same files over and over,
/// so it swaps only the bytes of the one JSON string that changes and
/// leaves comments, ordering, indentation, line endings and the BOM alone.
/// Pure (bytes in, bytes out) so the LogicHarness runs it against the real
/// V6.0 and V7.0 files.
/// </remarks>
public static class AppSettingsConnectionEditor
{
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        // Keeps backslashes (.\SQLEXPRESS) and quotes escaped as JSON
        // requires, but not '+' '<' '&' etc. as \u escapes. Server-side
        // config, never HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    /// <summary>All string entries directly under the top-level "ConnectionStrings" object.</summary>
    public static IReadOnlyList<FoundConnection> Find(byte[] file)
    {
        int bom = HasBom(file) ? Utf8Bom.Length : 0;
        var span = new ReadOnlySpan<byte>(file, bom, file.Length - bom);
        var reader = new Utf8JsonReader(span, ReaderOptions);
        var found = new List<FoundConnection>();

        // depth 1 = top-level object's properties; the connection strings are at depth 2.
        bool inConnectionStrings = false;
        string? pendingName = null;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    if (reader.CurrentDepth == 1)
                    {
                        string name = reader.GetString()!;
                        // .NET configuration keys are case-insensitive.
                        if (name.Equals("ConnectionStrings", StringComparison.OrdinalIgnoreCase))
                        {
                            reader.Read();
                            inConnectionStrings = reader.TokenType == JsonTokenType.StartObject;
                        }
                    }
                    else if (inConnectionStrings && reader.CurrentDepth == 2)
                    {
                        pendingName = reader.GetString();
                    }
                    break;

                case JsonTokenType.String when inConnectionStrings && reader.CurrentDepth == 2 && pendingName is not null:
                    found.Add(new FoundConnection(
                        pendingName,
                        reader.GetString()!,
                        bom + reader.TokenStartIndex,
                        // ValueSpan is the raw (still escaped) text between the quotes.
                        reader.ValueSpan.Length + 2));
                    pendingName = null;
                    break;

                case JsonTokenType.EndObject when inConnectionStrings && reader.CurrentDepth == 1:
                    inConnectionStrings = false;
                    break;

                default:
                    if (reader.CurrentDepth == 2) pendingName = null;
                    break;
            }
        }
        return found;
    }

    /// <summary>
    /// The entry the app actually reads: the expected name if present (any
    /// case), otherwise the only entry if there is exactly one.
    /// </summary>
    public static FoundConnection? Pick(IReadOnlyList<FoundConnection> found, string expectedName)
    {
        var exact = found.LastOrDefault(f => f.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>Returns the file with one connection string's value replaced; everything else byte-for-byte unchanged.</summary>
    public static byte[] Replace(byte[] file, FoundConnection target, string newValue)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(newValue, WriteOptions));
        var result = new byte[file.Length - target.ByteLength + encoded.Length];
        Buffer.BlockCopy(file, 0, result, 0, (int)target.ByteOffset);
        Buffer.BlockCopy(encoded, 0, result, (int)target.ByteOffset, encoded.Length);
        int tailStart = (int)target.ByteOffset + target.ByteLength;
        Buffer.BlockCopy(file, tailStart, result, (int)target.ByteOffset + encoded.Length, file.Length - tailStart);
        return result;
    }

    public static bool HasBom(byte[] file) =>
        file.Length >= 3 && file[0] == Utf8Bom[0] && file[1] == Utf8Bom[1] && file[2] == Utf8Bom[2];
}
