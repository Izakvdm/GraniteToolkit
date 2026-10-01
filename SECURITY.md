# Security design

The toolkit runs as administrator on client servers and changes IIS, Windows features, certificates, firewall rules, scheduled tasks and SQL Server. This file explains what it protects against, how, and what an operator and a release builder need to do.

## What we protect against

| Risk | Control |
|---|---|
| Someone swaps an exe or plants a DLL that the elevated toolkit then runs | The MSI installs to `C:\Program Files\Granite Toolkit`, which only administrators can change. The launcher warns in red if it's running from a folder ordinary users can change, such as a portable copy in Downloads. |
| A module exe gets replaced with something else | A signed launcher only starts modules signed by the same publisher, after checking with Windows' own `WinVerifyTrust`. The exe is held open (read-only sharing) from the check until it starts, so it can't be swapped in between. Broken signatures are always blocked, even in development builds. |
| Antivirus and SmartScreen flag the tool | Every exe, dll and the MSI are Authenticode-signed and timestamped. The app ships as normal files instead of a single-file exe that unpacks into `%TEMP%`. |
| Files planted in ProgramData (any user can create folders there by default) | Toolkit data folders are made admin-only (owner Administrators, inheritance cut, Administrators and SYSTEM only). A folder that a non-administrator created first and filled is refused. One that was left open is locked down, and anything in it is discarded and created again. |
| Install Wizard: a tampered release extraction gets deployed into IIS | Its extraction folder (`C:\ProgramData\Granite Install Wizard`) is admin-only before every extraction. If it was open, earlier extractions are deleted rather than reused. |
| BI: the scheduled sync wrapper gets edited, or a fake `sqlcmd.exe` is dropped beside it, and then runs under the task's account with highest privileges | `Run_BI_Sync.bat` is written to `C:\ProgramData\Granite BI Deploy\Tasks\<BI database>` (admin-only), and the task runs in that folder. Before v1.6.1 it was written to the script folder. |
| DB Switcher: edited settings point an administrator's SQL sign-in at another server | Settings are only read from an admin-only folder. Settings from a folder that was open are ignored. |
| A vulnerable NuGet package ships | Every package version is set in one place (`Directory.Packages.props`, with transitive pinning). A signed release build stops if `dotnet list package --vulnerable --include-transitive` reports anything, or if the check can't run. |
| Two modules ship different copies of the same DLL | The release build publishes each module on its own, then merges them, and stops if any file differs. |
| Developer tools end up on client servers | DB Switcher is a separate MSI feature, off by default, and is left out of the portable zip. The launcher only shows tools that are installed, and asks for confirmation before opening a developer tool. |
| Two operators run installers against the same IIS at once | One toolkit per machine (a global mutex), and one module at a time. |

What the launcher itself does is read-only. It lists IIS with `appcmd`, reads the registry and file versions, and queries Task Scheduler. It never connects to SQL Server, never asks for or stores credentials, and changes nothing. The modules do the changing, and only after the operator confirms.

### Limits worth knowing

- Revocation is checked from the local cache only, so an offline server doesn't hang. A certificate that's known to be revoked always fails. When nothing is cached, the signature check still runs and the log says revocation wasn't checked.
- Publishers are compared by the certificate subject, because Azure Artifact Signing issues a new certificate every day. Getting another certificate with the same subject means passing the certificate authority's identity validation for that organisation.
- The launcher checks the module exe. The DLLs next to it are protected by the install folder's permissions, not by a per-file check at launch. Windows App Control (WDAC) with a publisher rule covers DLLs too, if a client wants that.
- Logs are kept after uninstall, on purpose, as a record of what was done on the server. They hold server, site and account names, never passwords: the Install Wizard masks passwords, and the launcher never handles any.

## For operators

1. Install from the signed MSI, not a copy someone emailed. Check the signature first: right-click the MSI, open Properties, then Digital Signatures. It should show your publisher with a valid timestamp.
2. Compare `SHA256SUMS.txt` from the release folder if the file came through another channel: `Get-FileHash .\GraniteToolkit-x.y.z.msi`.
3. On client servers, install without the developer tools (the default). On a developer machine, run `msiexec /i GraniteToolkit-x.y.z.msi ADDLOCAL=ALL`.
4. If the dashboard shows a red banner, stop and fix it before using the modules.
5. Don't ask a client to add antivirus exclusions. If a signed build is flagged, report it to Microsoft as a false positive: https://www.microsoft.com/wdsi/filesubmission

## For whoever builds releases

1. Sign up for **Azure Artifact Signing**, or use an OV/EV code signing certificate on a hardware token. The certificate's organisation becomes the publisher everyone sees, so agree it with the business first.
2. For Artifact Signing, copy `build\signing.example.json` to `build\signing.json` and fill it in. It holds no secrets (sign in with `az login`) and is git-ignored. You also need signtool 10.0.2261.755 or later, from the Windows SDK, and the `Microsoft.ArtifactSigning.Client` dlib.
3. Commit everything, then run `Publish.cmd -Sign ArtifactSigning -Publisher "Exact Company Name"`. The build refuses to run from a tree with uncommitted changes, to skip the tests, or to continue if any file ends up unsigned, untimestamped or signed by a different publisher.
4. Keep the whole `artifacts\release\<version>` folder (MSI, zip, `SHA256SUMS.txt` and `build-info.json`, which records the commit). That's what lets you show later exactly what was installed where.

Report a security problem in the toolkit to the toolkit's maintainer directly, not in a client ticket.
