using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.Core;

/// <summary>The rewritten file and a readable list of what changed, for the log.</summary>
public sealed record AppSettingsResult(string Json, IReadOnlyList<string> Changes);

/// <summary>
/// Rewrites one component's appsettings.json from the wizard's answers.
/// Pure: takes the shipped file's text and returns new text, so the
/// LogicHarness can run it against the real release files.
/// </summary>
/// <remarks>
/// Only the keys below are touched; everything else (the ServiceStack
/// licence, Telemetry, Logging, the label printing defaults) is carried
/// over as shipped. The shipped files contain // comment lines (the
/// Business API's explains the date format pairing), which are dropped in
/// the rewrite -- JSON has no comments, and System.Text.Json can skip them
/// on read but not write them back. The installer keeps the untouched
/// original next to it as appsettings.json.orig.
/// </remarks>
public static class AppSettingsWriter
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        // Required: values added to a JsonNode (the new connection strings,
        // origins) are serialised through these options, and custom options
        // without a resolver throw "must specify a TypeInfoResolver" in
        // .NET 8. Caught by the LogicHarness against the real files.
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        WriteIndented = true,
        // Keeps dd'/'MM'/'yyyy and connection strings readable instead of
        // '-escaped. The files are server-side config, never HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppSettingsResult Apply(GraniteComponent component, string shippedJson, InstallContext context)
    {
        var root = JsonNode.Parse(shippedJson, documentOptions: ReadOptions)?.AsObject()
                   ?? throw new InvalidDataException($"{component.ReleaseFolder}\\appsettings.json is empty.");
        var changes = new List<string>();
        string apiUrl = context.UrlFor(GraniteComponent.BusinessApi);

        switch (component.Key)
        {
            case GraniteComponent.BusinessApi:
            {
                // Pool sizes kept exactly as the release ships them.
                SetConnection(root, "CONNECTION", AppConnection(context, ("Pooling", "true"), ("Min Pool Size", "5"), ("Max Pool Size", "25")));
                string[] origins = context.OriginsFor(GraniteComponent.WebDesktop, GraniteComponent.ProcessApp);
                root["AllowedOrigins"] = ToArray(origins);
                string dotNetFormat = DateFormatConverter.ToDotNet(context.DateFormat);
                root["DateTimeFormat"] = dotNetFormat;
                changes.Add("ConnectionStrings.CONNECTION");
                changes.Add($"AllowedOrigins ({origins.Length})");
                changes.Add($"DateTimeFormat = {dotNetFormat}");
                break;
            }

            case GraniteComponent.Custodian:
            {
                SetConnection(root, "CONNECTION", AppConnection(context));
                changes.Add("ConnectionStrings.CONNECTION");

                // Granite_Test ships pointing at a developer's GraniteDatabaseTest
                // on .\SQL2022 with the developer's password. Keep the catalog
                // name (Custodian's code may use it; nothing documents how) but
                // point it at this server and login, so no dev credentials are
                // left behind on a client server.
                if (root["ConnectionStrings"]?["Granite_Test"]?.GetValue<string>() is string testConn)
                {
                    string catalog = ReadKeyword(testConn, "Initial Catalog") ?? ReadKeyword(testConn, "Database") ?? context.DatabaseName + "Test";
                    SetConnection(root, "Granite_Test", ConnectionStringFormatter.ForApp(context.SqlServer, catalog, context.AppLogin, context.AppPassword));
                    changes.Add("ConnectionStrings.Granite_Test (server and login)");
                }

                string[] origins = context.OriginsFor(GraniteComponent.WebDesktop);
                root["AllowedOrigins"] = ToArray(origins);
                changes.Add($"AllowedOrigins ({origins.Length})");
                break;
            }

            case GraniteComponent.ProcessApp:
            {
                SetConnection(root, "ConnectionString", AppConnection(context));
                root["BusinessApiEndPoint"] = apiUrl;
                root["Company"] = context.CompanyName;
                changes.Add("ConnectionStrings.ConnectionString");
                changes.Add($"BusinessApiEndPoint = {apiUrl}");
                changes.Add($"Company = {context.CompanyName}");
                // V7.0 added DateTimeFormat to Process App (a .NET format
                // string, like the Business API's). V6.0 doesn't have it, so
                // it's only set when the shipped file has the key.
                if (root.ContainsKey("DateTimeFormat"))
                {
                    string paFormat = DateFormatConverter.ToDotNet(context.DateFormat);
                    root["DateTimeFormat"] = paFormat;
                    changes.Add($"DateTimeFormat = {paFormat}");
                }
                break;
            }

            case GraniteComponent.WebDesktop:
            {
                string custodianUrl = context.IsEnabled(GraniteComponent.Custodian) ? context.UrlFor(GraniteComponent.Custodian) : string.Empty;
                root["Business_API_Endpoint"] = apiUrl;
                root["URL_Custodian"] = custodianUrl;
                // Label printing and Integration are not part of the core
                // stack. The shipped values ("http://:5000/") are malformed
                // placeholders, so they're cleared rather than left pointing
                // at nothing.
                root["URL_LabelPrint"] = string.Empty;
                root["URL_Integration"] = string.Empty;
                root["CompanyName"] = context.CompanyName;
                root["DateTimeFormat"] = context.DateFormat;
                changes.Add($"Business_API_Endpoint = {apiUrl}");
                changes.Add($"URL_Custodian = {(custodianUrl.Length == 0 ? "(not installed)" : custodianUrl)}");
                changes.Add("URL_LabelPrint and URL_Integration cleared (not in the core stack)");
                changes.Add($"CompanyName = {context.CompanyName}");
                changes.Add($"DateTimeFormat = {context.DateFormat}");
                break;
            }

            default:
                throw new NotSupportedException($"No appsettings rules for component {component.Key}.");
        }

        return new AppSettingsResult(root.ToJsonString(WriteOptions), changes);
    }

    private static string AppConnection(InstallContext context, params (string Key, string Value)[] extra) =>
        ConnectionStringFormatter.ForApp(context.SqlServer, context.DatabaseName, context.AppLogin, context.AppPassword, extra);

    private static void SetConnection(JsonObject root, string name, string value)
    {
        if (root["ConnectionStrings"] is not JsonObject cs)
        {
            cs = new JsonObject();
            root["ConnectionStrings"] = cs;
        }
        cs[name] = value;
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (string v in values) array.Add(v);
        return array;
    }

    /// <summary>Reads one keyword from a connection string without depending on a SqlClient builder.</summary>
    public static string? ReadKeyword(string connectionString, string keyword)
    {
        foreach (string part in connectionString.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (string.Equals(part[..eq].Trim(), keyword, StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..].Trim();
        }
        return null;
    }
}
