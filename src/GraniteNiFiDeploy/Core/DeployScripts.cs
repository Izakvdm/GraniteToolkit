using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace GraniteNiFiDeploy.Core;

/// <summary>One data type NiFi can import: its Inbound folder, script and procs.</summary>
public sealed record Feed(string Name, string Script, string Title, string Description, bool IsOrder)
{
    public string StagingTable => $"Custom_{Name}Staging";
    public string ImportProc => $"Custom_Import{Name}";
}

public static class Feeds
{
    public static readonly IReadOnlyList<Feed> All = new[]
    {
        new Feed("MasterItem", "02_Feed_MasterItem.sql", "Items (MasterItem)", "Upserts items on Code, with audit.", false),
        new Feed("TradingPartner", "03_Feed_TradingPartner.sql", "Trading partners", "Upserts customers and suppliers on Code and document type.", false),
        new Feed("SalesOrder", "04_Feed_SalesOrder.sql", "Sales orders", "Creates order documents and lines. Insert only.", true),
        new Feed("PurchaseOrder", "05_Feed_PurchaseOrder.sql", "Purchase orders", "Creates receiving documents and lines. Insert only.", true)
    };

    public static Feed Get(string name) => All.First(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The Document Type, Status and Site an order feed stamps on new
/// documents. Wrong values import cleanly but the documents never show in
/// WebDesktop, so the wizard reads the real values from the database and
/// writes the chosen ones into the proc's parameter defaults.
/// </summary>
public sealed record OrderDefaults(string DocumentType, string DocumentStatus, string Site, string PartnerDocumentType)
{
    public static OrderDefaults ForSalesOrder => new("ORDER", "ENTERED", "", "ORDER");
    public static OrderDefaults ForPurchaseOrder => new("RECEIVING", "ENTERED", "", "RECEIVING");

    public bool IsValid(out string error)
    {
        foreach (var (name, value) in new[] { ("Document type", DocumentType), ("Status", DocumentStatus), ("Site", Site), ("Trading partner type", PartnerDocumentType) })
        {
            if (value.Length > 30) { error = $"{name} can be at most 30 characters."; return false; }
            if (value.Any(char.IsControl)) { error = $"{name} has a control character in it."; return false; }
        }
        if (DocumentType.Trim().Length == 0 || DocumentStatus.Trim().Length == 0) { error = "Document type and status can't be blank."; return false; }
        error = string.Empty;
        return true;
    }
}

/// <summary>
/// The SQL that NiFi Deploy runs, read from the resources embedded in the
/// signed exe (never from loose files on the server), and the small text
/// changes the wizard makes to it.
/// </summary>
public static class DeployScripts
{
    public const string FrameworkScript = "01_Framework.sql";
    public const string FlowResource = "NiFiDeploy.flow.GraniteCsvImport.json";

    private static readonly Regex GoLine = new(@"^\s*GO\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ReadSql(string scriptName) => ReadText("NiFiDeploy.sql." + scriptName);

    /// <summary>
    /// The properties of the first controller service of the given type in a
    /// flow definition (e.g. Password = #{granite.db.password}), or an empty map.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FlowServiceProperties(byte[] flowJson, string typeSuffix)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(flowJson);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var services = root?["flowContents"]?["controllerServices"]?.AsArray();
        var service = services?.FirstOrDefault(s => (s?["type"]?.GetValue<string>() ?? "").EndsWith(typeSuffix, StringComparison.Ordinal));
        foreach (var kv in service?["properties"]?.AsObject() ?? new System.Text.Json.Nodes.JsonObject())
            if (kv.Value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string? s) && s is not null)
                result[kv.Key] = s;
        return result;
    }

    public static byte[] ReadFlow()
    {
        using var stream = Open(FlowResource);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string ReadText(string resource)
    {
        using var reader = new StreamReader(Open(resource), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static Stream Open(string resource) =>
        typeof(DeployScripts).Assembly.GetManifestResourceStream(resource)
        ?? throw new InvalidOperationException($"Embedded resource {resource} is missing from this build.");

    /// <summary>The scripts to run, in order: the framework, then the chosen feeds.</summary>
    public static IReadOnlyList<string> ScriptsFor(IEnumerable<string> feedNames)
    {
        var chosen = new HashSet<string>(feedNames, StringComparer.OrdinalIgnoreCase);
        return new[] { FrameworkScript }.Concat(Feeds.All.Where(f => chosen.Contains(f.Name)).Select(f => f.Script)).ToList();
    }

    /// <summary>
    /// GO-separated batches. GO counts only on a line of its own (the
    /// scripts never put it in strings or comments, and have no GO n).
    /// </summary>
    public static IReadOnlyList<string> SplitBatches(string scriptText)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        foreach (string line in scriptText.Replace("\r\n", "\n").Split('\n'))
        {
            if (GoLine.IsMatch(line))
            {
                if (current.ToString().Trim().Length > 0) batches.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(line).Append('\n');
        }
        if (current.ToString().Trim().Length > 0) batches.Add(current.ToString());
        return batches;
    }

    /// <summary>
    /// Writes the chosen defaults into an order feed script's parameter
    /// lines (<c>@DocumentType varchar(30) = 'ORDER'</c> and so on). Throws
    /// if any of the four lines isn't found exactly once, so a changed
    /// script fails loudly instead of deploying the old defaults.
    /// </summary>
    public static string ApplyOrderDefaults(string scriptText, OrderDefaults defaults)
    {
        if (!defaults.IsValid(out string error)) throw new ArgumentException(error);
        string text = scriptText;
        text = ReplaceDefault(text, "DocumentType", defaults.DocumentType);
        text = ReplaceDefault(text, "DocumentStatus", defaults.DocumentStatus);
        text = ReplaceDefault(text, "Site", defaults.Site);
        text = ReplaceDefault(text, "PartnerDocumentType", defaults.PartnerDocumentType);
        return text;
    }

    private static string ReplaceDefault(string text, string parameter, string value)
    {
        var pattern = new Regex(@"(?m)^(\s*@" + parameter + @"\s+varchar\(30\)\s*=\s*)'(?:[^']|'')*'");
        int count = pattern.Matches(text).Count;
        if (count != 1) throw new InvalidDataException($"Expected one default for @{parameter} in the order script, found {count}.");
        string literal = "'" + value.Replace("'", "''") + "'";
        return pattern.Replace(text, m => m.Groups[1].Value + literal, 1);
    }

    /// <summary>The script text for a feed with its defaults applied (order feeds only).</summary>
    public static string FeedScript(Feed feed, OrderDefaults? defaults)
    {
        string text = ReadSql(feed.Script);
        return feed.IsOrder && defaults is not null ? ApplyOrderDefaults(text, defaults) : text;
    }
}
