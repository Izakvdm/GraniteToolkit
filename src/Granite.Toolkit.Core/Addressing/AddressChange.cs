using Granite.Toolkit.Core.Discovery;
using Granite.Toolkit.Core.Json;

namespace Granite.Toolkit.Core.Addressing;

/// <summary>One appsettings.json value an app uses to find the Business API.</summary>
public sealed record EndpointSetting(GraniteAppKind App, string Key, string Url)
{
    public string? Host => GraniteAddress.HostOf(Url);
}

/// <summary>One value the change will rewrite, for the preview and the log.</summary>
public sealed record SettingChange(GraniteAppKind App, string Setting, string Old, string New);

/// <summary>A file's bytes before and after.</summary>
public sealed record FileEdit(GraniteAppKind App, string Path, byte[] Original, byte[] Updated);

/// <summary>Everything a change of address will do to the config files.</summary>
public sealed record AddressPlan(
    string NewHost,
    IReadOnlyList<string> OldHosts,
    IReadOnlyList<SettingChange> Changes,
    IReadOnlyList<FileEdit> Edits,
    IReadOnlyList<string> Notes)
{
    public bool NothingToDo => Changes.Count == 0;
}

/// <summary>
/// Where each Granite app keeps the Business API's address, and the plan
/// for moving an install to a new address. Pure: file bytes in, file bytes
/// out, so the harness checks it against the real V6.0 files.
/// </summary>
/// <remarks>
/// The settings are the ones the Install Wizard writes (AppSettingsWriter):
/// Web Desktop's Business_API_Endpoint and URL_Custodian, Process App's
/// BusinessApiEndPoint, and the AllowedOrigins (CORS) lists of the Business
/// API and Custodian. Origins that used the old address are moved to the
/// new one when the old address no longer belongs to this machine: left in,
/// an address that may now be someone else's computer would stay trusted
/// by the API. Origins for addresses this machine still has are kept, so
/// scanners still using them carry on working. Every origin is written in
/// lower case without a trailing slash: the APIs compare the list letter
/// for letter with what the browser sends, which is always lower case.
/// </remarks>
public static class AddressChange
{
    public static readonly IReadOnlyDictionary<GraniteAppKind, string[]> EndpointKeys = new Dictionary<GraniteAppKind, string[]>
    {
        [GraniteAppKind.WebDesktop] = new[] { "Business_API_Endpoint", "URL_Custodian" },
        [GraniteAppKind.ProcessApp] = new[] { "BusinessApiEndPoint" }
    };

    public static readonly IReadOnlyList<GraniteAppKind> OriginApps = new[] { GraniteAppKind.BusinessApi, GraniteAppKind.Custodian };

    public const string OriginsKey = "AllowedOrigins";

    /// <summary>The non-empty endpoint settings in one app's appsettings.json.</summary>
    public static IReadOnlyList<EndpointSetting> ReadEndpoints(GraniteAppKind app, byte[] file)
    {
        if (!EndpointKeys.TryGetValue(app, out var keys)) return Array.Empty<EndpointSetting>();
        var list = new List<EndpointSetting>();
        foreach (string key in keys)
        {
            string? value = JsonTextEditor.GetString(file, key);
            if (!string.IsNullOrWhiteSpace(value)) list.Add(new EndpointSetting(app, key, value.Trim()));
        }
        return list;
    }

    /// <summary>
    /// The plan for pointing an install at <paramref name="newHost"/>.
    /// <paramref name="files"/> holds each app's appsettings.json (path and
    /// bytes); apps that aren't installed are simply absent.
    /// </summary>
    public static AddressPlan Plan(IReadOnlyDictionary<GraniteAppKind, (string Path, byte[] Bytes)> files, string newHost, MachineAddresses machine)
    {
        if (!GraniteAddress.IsValidNewHost(newHost, out string error)) throw new ArgumentException(error, nameof(newHost));
        newHost = newHost.Trim();

        var changes = new List<SettingChange>();
        var edits = new List<FileEdit>();
        var notes = new List<string>();

        // 1. Endpoints: every one moves to the new host.
        var endpoints = files.SelectMany(f => ReadEndpoints(f.Key, f.Value.Bytes)).ToList();
        var oldHosts = endpoints.Select(e => e.Host).OfType<string>()
            .Where(h => !h.Equals(newHost, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var (app, (path, bytes)) in files)
        {
            byte[] updated = bytes;
            foreach (var setting in ReadEndpoints(app, bytes))
            {
                if (setting.Host is null)
                {
                    notes.Add($"{Title(app)} {setting.Key} = \"{setting.Url}\" isn't a URL; left as it is.");
                    continue;
                }
                string newUrl = GraniteAddress.WithHost(setting.Url, newHost);
                if (newUrl == setting.Url) continue;
                updated = JsonTextEditor.SetString(updated, setting.Key, newUrl);
                changes.Add(new SettingChange(app, setting.Key, setting.Url, newUrl));
            }

            // 2. CORS origins on the APIs.
            if (OriginApps.Contains(app) && JsonTextEditor.GetStringArray(updated, OriginsKey) is { } origins)
            {
                var stale = oldHosts.Where(h => GraniteAddress.Check(h, machine) == AddressHealth.NotThisMachine)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var result = new List<string>();
                void Add(string origin)
                {
                    if (!result.Contains(origin, StringComparer.OrdinalIgnoreCase)) result.Add(origin);
                }
                // Every origin is written the way browsers send it (lower case, no
                // trailing slash), because the APIs match the list letter for letter.
                foreach (string origin in origins)
                {
                    string? host = GraniteAddress.HostOf(origin);
                    Add(GraniteAddress.NormalizeOrigin(host is not null && stale.Contains(host) ? GraniteAddress.WithHost(origin, newHost) : origin));
                }
                // The new host must be allowed on every port an origin already names (Web Desktop, Process App).
                foreach (var port in origins.Select(o => Uri.TryCreate(o, UriKind.Absolute, out var u) ? (u.Scheme, u.Port) : default)
                             .Where(p => p.Scheme is "https" or "http").Distinct())
                    Add(GraniteAddress.NormalizeOrigin($"{port.Scheme}://{newHost}:{port.Port}"));

                if (!result.SequenceEqual(origins))
                {
                    updated = JsonTextEditor.SetStringArray(updated, OriginsKey, result);
                    foreach (string old in origins)
                    {
                        string normal = GraniteAddress.NormalizeOrigin(old);
                        if (!result.Contains(normal))
                            changes.Add(new SettingChange(app, OriginsKey, old, "(removed: that address isn't this machine any more)"));
                        else if (normal != old)
                            changes.Add(new SettingChange(app, OriginsKey, old, normal + "  (as browsers send it)"));
                    }
                    var normalizedOld = origins.Select(GraniteAddress.NormalizeOrigin).ToHashSet();
                    foreach (string added in result.Where(r => !normalizedOld.Contains(r)))
                        changes.Add(new SettingChange(app, OriginsKey, "(added)", added));
                }
            }
            else if (OriginApps.Contains(app))
            {
                notes.Add($"{Title(app)} has no {OriginsKey} list; nothing to update there.");
            }

            if (!ReferenceEquals(updated, bytes)) edits.Add(new FileEdit(app, path, bytes, updated));
        }

        if (endpoints.Count == 0) notes.Add("No Business API address found in Web Desktop or Process App settings.");
        return new AddressPlan(newHost, oldHosts, changes, edits, notes);
    }

    public static string Title(GraniteAppKind kind) => kind switch
    {
        GraniteAppKind.WebDesktop => "Web Desktop",
        GraniteAppKind.BusinessApi => "Business API",
        GraniteAppKind.ProcessApp => "Process App",
        GraniteAppKind.Custodian => "Custodian",
        _ => kind.ToString()
    };
}
