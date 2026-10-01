# GraniteWMS Toolkit

One install for the GraniteWMS installer tools. A dashboard shows what's on the server and opens the right module: Install GraniteWMS, Deploy BI reporting, Add Granite Attach, plus DB Switcher as an optional developer tool. All of them share one core library and ship as one signed MSI.

> **Status (v0.1.0, 2026-10-01): launcher, packaging and security work done; not yet run on Windows.**
> All 12 projects build with **0 warnings, 0 errors**. Harnesses: Core **21/0**, Launcher **50/0**, DB Switcher **275/0**, Install Wizard **165/0** (V6.0 release), BI Deploy **218/0** (real BI scripts).
> The dashboard was published as a self-contained win-x64 build and run under Wine: it read the machine, showed every module and started the Install Wizard through the signature check. Wine isn't Windows, so the first real run, the MSI build (WiX can't be downloaded here) and signing are still to do. See "Confirm on the next run".

Security design and the release checklist are in [SECURITY.md](SECURITY.md).

## What's in it

| Project | What it is |
|---|---|
| `src/GraniteToolkit` | The dashboard and launcher, `GraniteToolkit.exe` |
| `src/Granite.Toolkit.Core` | Shared code: processes, logging, IIS commands, SQL instance discovery, Granite install discovery, prerequisite checks, signature and folder security |
| `src/Granite.Toolkit.UI` | Shared Windows Forms wizard base class |
| `src/GraniteInstallWizard` | Core stack installer, v0.5.2 |
| `src/GraniteBiDeployWizard` | BI deployment wizard, v1.6.1 |
| `src/GraniteAttachInstaller` | Granite Attach installer, v0.1.1 |
| `src/GraniteDbSwitcher` | Developer tool, v0.1.1 |
| `tests/Harness.*` | Logic harnesses that run with only the .NET SDK |
| `installer/` | The WiX MSI (Package.wxs; the file list is generated at build time) |
| `build/` | `Publish.ps1` (the release build) and `Signing.psm1` |

## The dashboard

`GraniteToolkit.exe` (Start menu: GraniteWMS Toolkit) runs as administrator and shows two things.

- **This server (read-only):** IIS and URL Rewrite, the ASP.NET Core 8 Hosting Bundle, local SQL Server instances, each GraniteWMS install found in IIS with its version and apps, Granite Attach, and the BI sync scheduled task. "Copy summary" copies it as text for a ticket or email.
- **Tools:** a tile for each module installed next to it, with a line about this server ("No GraniteWMS install found. Start here.", "Already installed", and so on). Opening a module checks its signature first. One module runs at a time, and the status refreshes when it closes.

Banners at the top flag a development (unsigned) build, a toolkit running from a folder ordinary users can change, or a log folder that couldn't be secured.

The launcher log is in `C:\ProgramData\Granite Toolkit\Logs`.

## Building

On Windows with the .NET 8 SDK or later:

| Command | Does |
|---|---|
| `Build-And-Test.cmd` | Builds everything and runs the harnesses that need no extra data |
| `Publish.cmd` | Development build in `artifacts\release\<version>`: unsigned MSI, portable zip, checksums |
| `Publish.cmd -Sign ArtifactSigning -Publisher "Exact Company Name"` | Signed release (setup in SECURITY.md) |
| `Publish.cmd -Sign CertificateStore -CertificateThumbprint <sha1> -TimestampUrl <url> -Publisher "..."` | Signed with a certificate in the Windows store, e.g. on a hardware token |

What `build\Publish.ps1` does, stopping at the first problem:
1. Checks for uncommitted changes (signed releases only) and for packages with known vulnerabilities.
2. Builds the solution and runs the harnesses.
3. Publishes the five apps, self-contained for win-x64, as normal files rather than single-file exes. Then merges them into one folder, stopping if two modules ship different copies of a file.
4. Signs and timestamps every exe and dll that isn't already signed, then verifies all of them.
5. Builds the MSI from the signed files, signs it and verifies it.
6. Writes the portable zip (without DB Switcher), `SHA256SUMS.txt` and `build-info.json`.

The MSI build downloads the WiX v5 SDK from NuGet the first time. WiX v5 is free under its MS-RL licence. WiX v6 and later ask commercial users to pay the Open Source Maintenance Fee, so staying on v5 is deliberate until that's decided.

Package versions are all in `Directory.Packages.props`. Module versions stay in each module's csproj, and the toolkit and MSI version is in `src/GraniteToolkit/GraniteToolkit.csproj`.

## Installing

- **Client server:** run `GraniteToolkit-x.y.z.msi`. It installs to `C:\Program Files\Granite Toolkit`, adds a Start menu shortcut, and creates an admin-only `C:\ProgramData\Granite Toolkit`.
- **Developer machine:** `msiexec /i GraniteToolkit-x.y.z.msi ADDLOCAL=ALL` adds DB Switcher.
- **Upgrade:** run the newer MSI. Downgrades are refused.
- **No install allowed:** unzip the portable zip into a folder only administrators can change, such as `C:\Tools\Granite Toolkit`. The dashboard warns if the folder is writable by others.

## Testing

| Harness | Run with | Needs |
|---|---|---|
| `Harness.Core` | `dotnet run --project tests\Harness.Core -c Release` | nothing |
| `Harness.Launcher` | `dotnet run --project tests\Harness.Launcher -c Release` | nothing |
| `Harness.DbSwitcher` | `dotnet run --project tests\Harness.DbSwitcher -c Release` | nothing (optionally a folder of real appsettings files) |
| `Harness.InstallWizard` | `dotnet run --project tests\Harness.InstallWizard -c Release -- "C:\Users\izakm\Documents\Granite WMS\Granite V6.0"` | a release folder or zip |
| `Harness.BiDeploy` | `dotnet run --project tests\Harness.BiDeploy -c Release` | the BI scripts at `/tmp/harness-scripts/` (path hard-coded, as before) |

## Changes in this version

**New**
- The dashboard and launcher (`GraniteToolkit.exe`), the MSI and the release build.
- Shared core: Granite install discovery (from DB Switcher) and the prerequisite checks (from the Install Wizard) now live in `Granite.Toolkit.Core`, so the dashboard uses the same logic. Signature checking, folder security and the toolkit paths are new there.
- Central package management. `System.Diagnostics.EventLog` is pinned to 8.0.1, because BI and the others shipped different copies.

**Security fixes in the modules** (details in SECURITY.md)
- **Install Wizard 0.5.2:** the release extraction and log folders in ProgramData are admin-only. An extraction from a folder that was open is never reused. A setup problem shows as an error on Step 6 instead of crashing the wizard.
- **BI Deploy 1.6.1:** `Run_BI_Sync.bat` now goes in `C:\ProgramData\Granite BI Deploy\Tasks\<BI database>` (admin-only), and the scheduled task runs there, not in the script folder. Re-running the wizard on a server updates the existing task to the new location. The old .bat in the script folder isn't used any more and can be deleted.
- **DB Switcher 0.1.1:** settings and logs are kept in an admin-only folder. Settings from a folder that was open are ignored.
- **Attach installer 0.1.1:** targets .NET 8, the same as the rest.

## Confirm on the next run

1. `Build-And-Test.cmd`, then `Publish.cmd`. Check that the MSI builds and note any ICE warnings.
2. Install the MSI on a test server and open the toolkit from the Start menu. Check the dashboard matches the server, and that `C:\ProgramData\Granite Toolkit` shows only Administrators and SYSTEM under Properties, Security.
3. Open each module from the dashboard and do a dry run of the Install Wizard.
4. BI on a test server with Windows Task Scheduler: confirm the task points at `C:\ProgramData\Granite BI Deploy\Tasks\...\Run_BI_Sync.bat` and a sync runs.
5. Once you have a certificate, do a signed build and check the MSI's Digital Signatures tab, the dashboard's "Signed by" badge, and that a module replaced with an unsigned copy is refused.

## Next

- Merge the Install Wizard's `GraniteSqlScriptParser` and BI's `ScriptBatchParser`, and share the log console and SQL connection helpers.
- A security pass through each module's own code: secrets in logs, and SQL permissions requested versus needed.
- NiFiDeploy as a module once that project has code.
