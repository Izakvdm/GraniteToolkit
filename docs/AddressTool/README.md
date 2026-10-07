# Change server address (GraniteAddressTool)

Moves an installed GraniteWMS stack to a new address, for when a server's IP changes or a DHCP address should be swapped for the server's name. Opened from the toolkit dashboard ("Change server address"), runs as administrator.

## What it changes

| App | Setting | Change |
|---|---|---|
| Web Desktop | `Business_API_Endpoint`, `URL_Custodian` | Host moved to the new address; port and path kept |
| Process App | `BusinessApiEndPoint` | Same |
| Business API, Custodian | `AllowedOrigins` | The new address added on every port already listed. Origins for an old address this server no longer has are removed (it may be another computer now); origins for addresses it still has are kept, so scanners still using them carry on working |
| Granite sites tied to one IP | IIS bindings | Bound to all addresses (protocol, port and host name kept), and their own `ip:port` certificate entries removed. A site tied to one IP stops answering when the IP changes and never answers over IPv6, which Windows tries first for its own name. The dashboard shows these red (old IP) or amber (current IP) |
| All Granite API origins | AllowedOrigins | Written lower case with no trailing slash, as browsers send them; the APIs compare letter for letter |
| All Granite HTTPS sites | Certificate | Every HTTPS port ends up on one certificate that covers the new address and this server trusts: an existing one if there is one (a covering self-signed certificate that isn't trusted yet is added to Trusted Root). Otherwise Only if the bound certificate doesn't cover the new address: a self-signed one is reissued with the old names (minus IPs the server no longer has) plus the new address, trusted on this server, exported to `<install>\Certificates` and bound to every Granite HTTPS port. A certificate from a real CA is never replaced: the tool stops and says which names it does cover |

Files are edited in place without being re-serialised: only the bytes of the changed values move, so comments, order and line endings stay as they were. Each changed file is backed up first to `C:\ProgramData\Granite Toolkit\Backups\address-<yyyyMMdd-HHmmss>\<app folder>`: admin-only, and never inside a folder IIS serves (the API's settings hold its database password). Then the install's app pools are recycled and the new URLs are requested from the server to check they answer.

If anything fails before the recycle, every step is undone, newest first: certificate entries, site bindings, then files. The old certificate stays in the store (remove it in certlm.msc once nothing uses it).

## Choosing the address

The suggestions list the server's names first, then fixed IPs, then DHCP IPs.

- **Fixed IP:** works for scanners on Wi-Fi with no DNS.
- **The server's name:** survives IP changes. Scanners need a DNS entry for it; Android devices often can't resolve Windows computer names on their own.
- **A DHCP IP:** only if it's reserved on the DHCP server. Windows can't tell a reserved address from any other DHCP one, so the tool warns either way.

localhost is refused: Web Desktop and the APIs only work for other devices with a real address.

## Related

- The dashboard reads the same settings (read-only) and shows a red "Business API address is out of date" row when they point at an IP the server no longer has, with the tile suggested.
- The Install Wizard's Step 4 now defaults to a fixed IP if the server has one, otherwise the server's name, and warns about DHCP addresses.
- The rules are in `Granite.Toolkit.Core/Addressing` and `Json/JsonTextEditor.cs`, checked by `tests/Harness.Core`.
