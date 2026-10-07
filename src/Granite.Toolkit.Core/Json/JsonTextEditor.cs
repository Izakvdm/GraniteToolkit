using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Granite.Toolkit.Core.Json;

/// <summary>
/// Reads and replaces top-level values in an appsettings.json without
/// re-serialising the file: only the bytes of the one value that changes
/// are swapped, so comments, key order, indentation, line endings and the
/// BOM stay exactly as they were. Same approach as the DB Switcher's
/// connection string editor, for any top-level string or string array.
/// Pure (bytes in, bytes out), so the harness checks it.
/// </summary>
public static class JsonTextEditor
{
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        // Escapes what JSON requires (quotes, backslashes, control
        // characters) but not '+', '<', '&' and so on: server-side config,
        // never HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    private sealed record Span(int Start, int Length, JsonTokenType Kind);

    /// <summary>The top-level string property, or null if it's missing or not a string.</summary>
    public static string? GetString(byte[] file, string property)
    {
        var span = Find(file, property);
        if (span is null || span.Kind != JsonTokenType.String) return null;
        return JsonSerializer.Deserialize<string>(new ReadOnlySpan<byte>(file, span.Start, span.Length), WriteOptions);
    }

    /// <summary>The top-level array of strings, or null if it's missing or not an array of strings.</summary>
    public static IReadOnlyList<string>? GetStringArray(byte[] file, string property)
    {
        var span = Find(file, property);
        if (span is null || span.Kind != JsonTokenType.StartArray) return null;
        try { return JsonSerializer.Deserialize<string[]>(new ReadOnlySpan<byte>(file, span.Start, span.Length), WriteOptions); }
        catch (JsonException) { return null; }
    }

    /// <summary>Replaces an existing top-level string value. Throws if the property isn't there or isn't a string.</summary>
    public static byte[] SetString(byte[] file, string property, string value)
    {
        var span = Find(file, property) ?? throw new InvalidDataException($"\"{property}\" isn't in the file.");
        if (span.Kind != JsonTokenType.String) throw new InvalidDataException($"\"{property}\" isn't a text value.");
        return Splice(file, span, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, WriteOptions)));
    }

    /// <summary>
    /// Replaces an existing top-level array with these strings, one per line,
    /// indented to match the property's own line. Throws if the property
    /// isn't there or isn't an array.
    /// </summary>
    public static byte[] SetStringArray(byte[] file, string property, IReadOnlyList<string> values)
    {
        var span = Find(file, property) ?? throw new InvalidDataException($"\"{property}\" isn't in the file.");
        if (span.Kind != JsonTokenType.StartArray) throw new InvalidDataException($"\"{property}\" isn't a list.");

        string newline = DetectNewline(file);
        string indent = IndentOfLine(file, span.Start);
        string inner = indent + (indent.Contains('\t') ? "\t" : "  ");
        string text = values.Count == 0
            ? "[]"
            : "[" + newline + string.Join("," + newline, values.Select(v => inner + JsonSerializer.Serialize(v, WriteOptions))) + newline + indent + "]";
        return Splice(file, span, Encoding.UTF8.GetBytes(text));
    }

    private static Span? Find(byte[] file, string property)
    {
        int bom = HasBom(file) ? Utf8Bom.Length : 0;
        var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(file, bom, file.Length - bom), ReaderOptions);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
            bool match = reader.ValueTextEquals(property);
            if (!reader.Read()) return null;
            if (!match)
            {
                reader.Skip();
                continue;
            }
            int start = (int)reader.TokenStartIndex;
            var kind = reader.TokenType;
            if (kind is JsonTokenType.StartArray or JsonTokenType.StartObject) reader.Skip();
            int end = (int)reader.BytesConsumed;
            return new Span(start + bom, end - start, kind);
        }
        return null;
    }

    private static byte[] Splice(byte[] file, Span span, byte[] replacement)
    {
        var result = new byte[file.Length - span.Length + replacement.Length];
        Buffer.BlockCopy(file, 0, result, 0, span.Start);
        Buffer.BlockCopy(replacement, 0, result, span.Start, replacement.Length);
        Buffer.BlockCopy(file, span.Start + span.Length, result, span.Start + replacement.Length, file.Length - span.Start - span.Length);
        return result;
    }

    private static bool HasBom(byte[] file) =>
        file.Length >= 3 && file[0] == Utf8Bom[0] && file[1] == Utf8Bom[1] && file[2] == Utf8Bom[2];

    private static string DetectNewline(byte[] file) => Array.IndexOf(file, (byte)'\r') >= 0 ? "\r\n" : "\n";

    /// <summary>The spaces and tabs at the start of the line that holds <paramref name="offset"/>.</summary>
    private static string IndentOfLine(byte[] file, int offset)
    {
        int lineStart = offset;
        while (lineStart > 0 && file[lineStart - 1] != (byte)'\n') lineStart--;
        var sb = new StringBuilder();
        for (int i = lineStart; i < offset && (file[i] == (byte)' ' || file[i] == (byte)'\t'); i++) sb.Append((char)file[i]);
        return sb.ToString();
    }
}
