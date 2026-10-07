using Granite.Toolkit.Core.Discovery;
using Granite.Toolkit.Core.Iis;

namespace Granite.Toolkit.Core.Addressing;

/// <summary>A Granite site binding tied to one IP address instead of all of them.</summary>
public sealed record PinnedBinding(GraniteApp App, IisBinding Binding, AddressHealth Health);

/// <summary>
/// Pure checks on the IIS bindings of an install's Granite sites. A site
/// bound to one IP (IIS Manager's "IP address" box, rather than "All
/// Unassigned") stops answering as soon as the machine's IP changes, never
/// answers over IPv6 (which Windows tries first for its own name), and
/// needs its own http.sys certificate entry for that IP.
/// </summary>
public static class SiteBindings
{
    public static bool IsAllAddresses(IisBinding b) => b.Address is "*" or "" or "0.0.0.0";

    private static bool IsWeb(IisBinding b) => b.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase) || b.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every binding of the install's sites that's tied to one address.</summary>
    public static IReadOnlyList<PinnedBinding> Pinned(GraniteInstall install, MachineAddresses machine) =>
        install.Apps
            .SelectMany(app => app.Bindings.Where(b => IsWeb(b) && !IsAllAddresses(b)).Select(b => new PinnedBinding(app, b, GraniteAddress.Check(b.Address, machine))))
            .ToList();

    /// <summary>
    /// The site's full binding list for appcmd ("https/*:40099:,http/*:80:")
    /// with every address set to all addresses. Host names, protocols and
    /// ports stay as they were; the other bindings are kept, since appcmd's
    /// /bindings replaces the whole list.
    /// </summary>
    public static string AllAddressesBindingList(IReadOnlyList<IisBinding> bindings) =>
        string.Join(",", bindings.Select(b => IsWeb(b) ? $"{b.Protocol}/*:{b.Port}:{b.HostName}" : $"{b.Protocol}/{b.Address}:{b.Port}:{b.HostName}")
            .Distinct(StringComparer.OrdinalIgnoreCase));
}
