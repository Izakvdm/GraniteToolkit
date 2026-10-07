# GraniteWMS Toolkit

One install for the GraniteWMS installer tools. A dashboard shows what's on the server and opens the right module: Install GraniteWMS, Deploy BI reporting, Deploy NiFi integration, plus DB Switcher as an optional developer tool. All of them share one core library and ship as one signed MSI.

> **Status (v0.2.0, 2026-10-02): NiFi Deploy added as a module; not yet run on Windows.**
> NiFi Deploy, the launcher changes and the shared-core changes were compiled with the C# compiler (Roslyn) against the .NET 8 reference assemblies and the Windows Forms and SqlClient assemblies from the 0.1.1 build: **0 warnings, 0 errors** for Core, UI, the dashboard, Attach and NiFi Deploy. Not yet built with `dotnet build` (no SDK or NuGet access in that session). Harnesses: Core **21/0**, Launcher **58/0**, NiFi Deploy **112/0**.
> NiFi Deploy's NiFi side was run for real against NiFi 2.11.0 (Linux, Java 25): its edits to the shipped nifi.cmd, nifi.properties and bootstrap.conf, the credentials tool with quotes, & and ! in the password, the certificate pin, sign-in, flow upload, parameters, NiFi's own SQL connection check, enable, start and clean-up. The Windows parts (NSSM, the service, ACLs) and the SQL deployment against a real Granite database are still to do. See "Confirm on the next run".
>
> **v0.1.0 (2026-10-01): launcher, packaging and security work done; not yet run on Windows.**
> All 12 projects build with **0 warnings, 0 errors**. Harnesses: Core **21/0**, Launcher **50/0**, DB Switcher **275/0**, Install Wizard **165/0** (V6.0 release), BI Deploy **218/0** (real BI scripts).
> The dashboard was published as a self-contained win-x64 build and run under Wine: it read the machine, showed every module and started the Install Wizard through the signature check. Wine isn't Windows, so the first real run, the MSI build (WiX can't be downloaded here) and signing are still to do. See "Confirm on the next run".

Security design and the release checklist are in [SECURITY.md](SECURITY.md).

## What's in it

| Project | What it is |
|---|---|
| `src/GraniteToolkit` | The dashboard and launcher, `GraniteToolkit.exe` |
| `src/Granite.Toolkit.Core` | Shared code: processes, logging, IIS commands, SQL instance discovery, Granite install discovery, prerequisite checks, signature and folder security |
| `src/Granite.Toolkit.UI` | Shared Windows Forms wizard base class |
| `src/GraniteInstallWizard` | Core stack installer, v0.7.0 |
| `src/GraniteBiDeployWizard` | BI deployment wizard, v1.6.1 |
| `src/GraniteNiFiDeploy` | NiFi Deploy: Apache NiFi and the Granite CSV import, v0.1.0 |
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

## NiFi Deploy

Installs Apache NiFi 2.x as a Windows service and sets up the Granite CSV import, end to end, from one wizard: a client system drops a CSV into `Inbound\<Feed>` and it lands in Granite, validated and audited. The design (staging tables, one import log, insert/update procs) comes from the NiFiDeploy project; this module is how it gets onto a server.

| Step | What you give it |
|---|---|
| 1. Install media | A folder, or a bundle zip, holding the NiFi 2.x zip, a JDK 21+ zip for Windows x64, NSSM, and the Microsoft SQL Server JDBC driver. Each zip is opened and checked (NiFi version, Java version from the JDK's release file, 64-bit nssm.exe, the right jre driver jar). Nothing is downloaded. |
| 2. NiFi service | Install folder, service name, port, heap, the NiFi sign-in, the import folder and (optional) the Windows account that drops files. Port, service name and folder clashes are checked here. |
| 3. Granite database | Server, sign-in and database. Test connection checks mixed-mode auth, permission to create logins and objects, and that it's a Granite database. Choose the feeds (MasterItem, TradingPartner, SalesOrder, PurchaseOrder); the order Type, Status and Site are offered from the values already in `dbo.Document`. |
| 4. Review and install | One button. |

What Install does, in order (SQL first, because that's where permissions usually fail and nothing on disk has changed yet):
1. Runs the framework and feed scripts (embedded in the exe) and creates or resets NiFi's SQL login with a random 32-character password, in role `Custom_NiFiImport` only.
2. Extracts NiFi and the JDK, writes `nifi-env.cmd`, removes `start /MIN` from `nifi.cmd`, sets the port and heap, sets the single-user login, copies the JDBC driver.
3. Creates `Inbound\<Feed>`, `Archive` and `Error`, admin-only, plus Modify on Inbound for the drop account.
4. Installs the NSSM service (automatic start), starts it and waits for the port.
5. Through NiFi's REST API: signs in, uploads the Granite CSV Import flow, sets its parameters (the SQL password as a sensitive value), has NiFi test its SQL connection, enables the services and starts the flow.
6. Writes a deployment record without passwords to `C:\ProgramData\Granite NiFi Deploy\Deployments`, and the full log to `...\Logs`.

If steps 2 to 4 fail, the service and NiFi folder this run created are removed, so the wizard can simply be run again. If NiFi can't reach SQL Server yet (TCP/IP off, SQL Browser off for a named instance, firewall), the install still finishes and the log says what to fix; imports retry on their own.

## Building

On Windows with the .NET 8 SDK or later:

| Command | Does |
|---|---|
| `Build-And-Test.cmd` | Builds everything and runs the harnesses that need no extra data |
| `Install-Local.cmd` | Installs the newest built MSI on this machine with DB Switcher, getting past the DisableMSI policy for the install only |
| `Run-DbSwitcher.cmd` | Builds DB Switcher and starts it, without installing anything |
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
- **Developer machine:** double-click `Install-Local.cmd`. It installs the newest MSI in `artifacts\release` with DB Switcher (`ADDLOCAL=ALL`), checks it against `SHA256SUMS.txt` and its signature first, and handles the error 1625 policy below for you. `-CoreOnly`, `-Msi <path>` and `-Uninstall` are optional. (By hand: `msiexec /i GraniteToolkit-x.y.z.msi ADDLOCAL=ALL`.)
- **Upgrade:** run the newer MSI. Downgrades are refused.
- **"The system administrator has set policies to prevent this installation" (error 1625):** the "Turn off Windows Installer" policy (`DisableMSI` under `HKLM\SOFTWARE\Policies\Microsoft\Windows\Installer`) blocks installs that weren't deployed through policy. On a client server, ask their IT to deploy the MSI (Intune or Group Policy), or use the portable zip. On your own machine, `Install-Local.cmd` does this automatically: it sets `DisableMSI` to 0 and restarts the Windows Installer service (which holds on to the old value) only for the length of the install, then always puts the value back exactly as it was, even if the install fails. The original value is saved under `HKLM\SOFTWARE\Granite Toolkit\PendingPolicyRestore` first, so a run that's killed midway is restored by the next run. If it's still refused, the script says whether AppLocker or Software Restriction Policies are the cause. `msiexec /i ... /l*v install.log` shows which policy refused it.
- **No install allowed:** unzip the portable zip into a folder only administrators can change, such as `C:\Tools\Granite Toolkit`. The dashboard warns if the folder is writable by others.

## Testing

| Harness | Run with | Needs |
|---|---|---|
| `Harness.Core` | `dotnet run --project tests\Harness.Core -c Release` | nothing |
| `Harness.Launcher` | `dotnet run --project tests\Harness.Launcher -c Release` | nothing |
| `Harness.DbSwitcher` | `dotnet run --project tests\Harness.DbSwitcher -c Release` | nothing (optionally a folder of real appsettings files) |
| `Harness.InstallWizard` | `dotnet run --project tests\Harness.InstallWizard -c Release -- "C:\Users\izakm\Documents\Granite WMS\Granite V6.0"` | a release folder or zip |
| `Harness.BiDeploy` | `dotnet run --project tests\Harness.BiDeploy -c Release` | the BI scripts at `/tmp/harness-scripts/` (path hard-coded, as before) |
| `Harness.NiFiDeploy` | `dotnet run --project tests\Harness.NiFiDeploy -c Release` | nothing |
| NiFi Deploy, live | `dotnet run --project tests\Harness.NiFiDeploy -c Release -- live <NiFi home> <user> <password> [port]` | a running NiFi 2.x **without** the Granite flow (it refuses to touch an existing Granite CSV Import context). Uploads the flow under a test name, sets parameters, runs NiFi's SQL check, starts, then stops and deletes it |

## Changes in this version

**0.3.2** (2026-10-06)
- **Install Wizard 0.7.0: install alongside an existing Granite install** (V7 next to V6, a test stack next to live). Step 4 has a new "Install alongside" option: it adds a suffix to the site names (" V7" from the release name, otherwise " 2", " 3"...), picks free ports, and switches "Replace existing IIS sites" off. A "Find free ports" button does the port part on its own, and Step 4 says straight away whether the chosen ports are free and, if not, what holds them.
- Ports are checked against the IIS sites on the server, anything else listening, and the port ranges Windows reserves for Hyper-V, WSL and Docker (`netsh int ipv4 show excludedportrange`), which IIS can't bind to. Suggestions stay between 1024 and 49151 and move as a block where possible (40080-40099 becomes 40180-40199), so the second install is easy to recognise.
- Pre-flight now blocks a port in a Windows reserved range, and, with "Install alongside" ticked, an install folder that already holds files (the existing install's folders would otherwise be moved to `.bak`). The review on Step 6 shows when an install is alongside, and profiles remember the option.

**0.3.1** (2026-10-05)
- **Install Wizard 0.6.0: the Custodian token is set on every install that includes Custodian, and nothing secret is bundled.** Before, it only reached the database through V6.0's `Hotfix\Custodian.md`, and only with the Hotfix database scripts ticked; V7.0 doesn't ship the file at all. Now it's its own step, from a Custodian.md picked on Step 3 (which replaces any token already in the database) or else the release's `Hotfix\Custodian.md` (which only fills a gap). The wizard carries no copy of its own: the token opens a shared GitHub repository, and a copy compiled into a signed exe would ship to every server and go stale (the 28 September token is already refused with "Bad credentials"). The token, key and version are read out of the file and written with a parameterised insert-or-update, so the file's SQL is never run as-is. Verification asks Custodian whether it can reach its process repository and says plainly when GitHub refuses the token; the dashboard shows the same check as a red row.

**0.3.0** (2026-10-05)
- **Granite Attach is no longer part of the toolkit.** It's a separate commercial product, so its installer moved back to the Granite Attach repo (as standalone v0.2.0) and isn't in the toolkit's solution, MSI or portable zip any more. The dashboard still shows, read-only, whether Attach is installed on a server, but has no tile for it. The harness checks that no Attach module is in the catalog. MSIs and zips from 0.1.0 to 0.2.1 still contain the Attach installer: don't hand those out.

**0.2.1** (first Windows run of NiFi Deploy, 2026-10-04)
- **Tested on Ultra (Windows 11, SQL Server 2022 Express, NiFi 2.11.0, Java 26):** the service installs and starts, NiFi connects to the Granite database, and all four sample files import (MasterItem 3 inserted, TradingPartner 2 inserted, PurchaseOrder 1 order and 2 lines, SalesOrder 2 orders and 3 lines). Orders whose trading partner doesn't exist yet are refused with a clear message, as designed. The two SQL fixes below were applied to Ultra by running the feed scripts by hand; the wizard itself still needs a rerun from a 0.2.1 build.
- **NiFi Deploy 0.1.1:** the service stopped straight away ("Unexpected status SERVICE_STOPPED in response to START control"). NSSM writes the service's output to `logs\service.log` but doesn't create the folder, and the NiFi zip has no `logs` folder (NiFi makes it on its first run). The `logs` and `run` folders are now created before the service is installed, and the harness checks that every NSSM output folder is on that list.
- **NiFi Deploy 0.1.1:** when the service fails to start, NSSM's own reason is read from the Application event log and shown in the wizard, and NiFi's logs are copied to `C:\ProgramData\Granite NiFi Deploy\Logs\failed-start-<time>` before the roll-back deletes the NiFi folder.
- **NiFi Deploy 0.1.1:** the SQL connection check always failed with "Login failed for user 'svc_granite_nifi'", even though the running flow connects. NiFi's API returns every set sensitive property as `********` and its verification uses the properties it's sent as they are, so the check was logging in with the password `********`. The check now sends the flow's `#{granite.db.password}` reference for masked sensitive properties, and a refused login gets its own advice (read the 18456 state with `xp_readerrorlog`) instead of the TCP/IP and firewall hint.
- **NiFi Deploy 0.1.1:** the first CSV import failed with error 468 (collation conflict) where the server's default collation (`SQL_Latin1_General_CP1_CI_AS`, so tempdb's) differs from the Granite database's (`Latin1_General_CI_AS`, set by the Granite create script). Every text column in the feed procedures' temp tables now says `COLLATE DATABASE_DEFAULT`, the pattern Granite's own views use; the harness checks all of them. Same fix in `Documents\Claude\NiFiDeploy\sql`.
- `Install-Local.cmd` installs a built MSI on a developer machine, lifting the DisableMSI policy for the install only. `Run-DbSwitcher.cmd` builds and starts DB Switcher without installing.
- Fixed the NiFi Deploy harness project file (a `--` inside an XML comment stopped the restore).

**0.2.0**
- New module: NiFi Deploy (`GraniteNiFiDeploy.exe`, v0.1.0), with its harness. The SQL scripts and the NiFi flow are embedded in the exe, so the signature covers them.
- Dashboard: an "Apache NiFi" row and a NiFi Deploy tile. NiFi services are found read-only, from the registry (NSSM services running `nifi.cmd`) and `sc query`.
- Shared core 0.2.0: `ProcessRunner.RunAsync` takes an optional working folder (NiFi's tools read `./conf`); `NiFiService` and `NiFiServiceDiscovery`.
- Release build, `Build-And-Test.cmd`, the solution and the MSI's feature text include the new module and harness.

**0.1.x**
- The dashboard and launcher (`GraniteToolkit.exe`), the MSI and the release build.
- Shared core: Granite install discovery (from DB Switcher) and the prerequisite checks (from the Install Wizard) now live in `Granite.Toolkit.Core`, so the dashboard uses the same logic. Signature checking, folder security and the toolkit paths are new there.
- Central package management. `System.Diagnostics.EventLog` is pinned to 8.0.1, because BI and the others shipped different copies.

**Security fixes in the modules** (details in SECURITY.md)
- **Install Wizard 0.5.2:** the release extraction and log folders in ProgramData are admin-only. An extraction from a folder that was open is never reused. A setup problem shows as an error on Step 6 instead of crashing the wizard.
- **BI Deploy 1.6.1:** `Run_BI_Sync.bat` now goes in `C:\ProgramData\Granite BI Deploy\Tasks\<BI database>` (admin-only), and the scheduled task runs there, not in the script folder. Re-running the wizard on a server updates the existing task to the new location. The old .bat in the script folder isn't used any more and can be deleted.
- **DB Switcher 0.1.1:** settings and logs are kept in an admin-only folder. Settings from a folder that was open are ignored.
- **Attach installer 0.1.1:** targets .NET 8, the same as the rest. (Moved out of the toolkit in 0.3.0.)

## Confirm on the next run

1. `Build-And-Test.cmd`, then `Publish.cmd`. Check that the MSI builds and note any ICE warnings.
2. Install the MSI on a test server and open the toolkit from the Start menu. Check the dashboard matches the server, and that `C:\ProgramData\Granite Toolkit` shows only Administrators and SYSTEM under Properties, Security.
3. Open each module from the dashboard and do a dry run of the Install Wizard.
4. BI on a test server with Windows Task Scheduler: confirm the task points at `C:\ProgramData\Granite BI Deploy\Tasks\...\Run_BI_Sync.bat` and a sync runs.
5. Once you have a certificate, do a signed build and check the MSI's Digital Signatures tab, the dashboard's "Signed by" badge, and that a module replaced with an unsigned copy is refused.

6. NiFi Deploy on a Windows test server with a Granite database: run all four steps, then check the service in services.msc (automatic, LocalSystem), that `C:\nifi` and `C:\GraniteImport` show only Administrators and SYSTEM (plus the drop account on Inbound), that `https://localhost:8443/nifi` shows the Granite CSV Import flow running, and that a sample CSV dropped into `Inbound\MasterItem` shows SUCCESS in `dbo.Custom_NiFiImportLog`. Then stop SQL Server's TCP/IP and run it again on a fresh folder and service name to see the "couldn't connect" follow-up, and make step 4 fail (stop the service mid-start) to check the roll-back.

## Next

- Merge the Install Wizard's `GraniteSqlScriptParser` and BI's `ScriptBatchParser`, and share the log console and SQL connection helpers.
- A security pass through each module's own code: secrets in logs, and SQL permissions requested versus needed.
