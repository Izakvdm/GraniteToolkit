# GraniteWMS Install Wizard

A .NET 8 Windows Forms wizard that installs the GraniteWMS core stack (V6.0 and V7.0) on a server: Web Desktop, Business API, Custodian API and Process App, with the Granite database, the IIS app pools and HTTPS sites, a certificate, and firewall rules. It replaces `Granite.Scaffolding.exe` and follows the same structure and conventions as the BI Deployment Wizard (sibling `Granite BI Deploy` repo).

> **Status (v0.5.1, 2026-09-29): first real install done on V6.0; reinstall supported, not yet run; V7.0 checked against the real release zip, not yet installed.**
> v0.2.0 was run end to end on Izak's machine (Ultra, SQL Server 2022 Express): prerequisites, certificate, all 553 batches of the create script plus the Hotfix scripts, files, appsettings, four IIS sites and firewall rules all completed, and Web Desktop and Process App answered HTTP 200. Business API and Custodian were reported as HTTP 503 by the verify stage; the logs showed that was a timing problem in the wizard, not a broken install (see the v0.2.1 entry below).
>
> `GraniteInstallWizard` (Debug and Release) and `LogicHarness` build with **0 warnings, 0 errors**. `LogicHarness`: **165 checks, 0 failed** against the real V6.0 release and **158, 0 failed** against the real V7.0 zip (the V6.0-only hotfix checks are skipped for V7.0). The code was compiled in the sandbox it was written in, using Izak's own NuGet cache as an offline package source and the WindowsDesktop build targets from his .NET SDK 10.0.401 (nuget.org is blocked there).
>
> v0.3.0's existing-database mode was tried on Step 3 against the first install's `GraniteDatabase`: it found the database, recognised it as Granite, and correctly stopped on the existing `Granite` login's password. That screen led to v0.3.1. Not yet confirmed: a full run of either database mode on v0.2.1 or later. See "Confirm on the next run".

## Building

On Windows with the .NET 8 SDK or later (Izak's machine has 10.0.401, which builds .NET 8 targets fine):

```
cd GraniteInstallWizard
dotnet restore
dotnet build -c Release
```

or open `GraniteInstallWizard.csproj` in Visual Studio 2022 and press F5. The app asks for elevation on launch (`app.manifest`): enabling Windows features, running the Hosting Bundle and URL Rewrite installers, writing to the LocalMachine certificate store and creating IIS sites all need it.

## Packaging

Same approach as the BI wizard: a portable, self-contained single `.exe` a consultant carries to each server, not an installer.

Double-click `Publish.cmd` in the repo root. It reads the version from the csproj, runs

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

and leaves `dist\GraniteInstallWizard-v<version>.exe` (the `dist` folder is git-ignored). The first publish downloads the .NET 8 Windows runtime packs from nuget.org, so it needs internet once. The title bar shows the version from the assembly, so "which build were you running" always has an answer.

For quick testing while developing, `Build-And-Run.cmd` does a normal Release build and starts the wizard straight away.

Copy the exe next to the release folder or zip on the server (or anywhere) and run it; it asks for administrator rights itself. It finds a `Granite V6.0` folder automatically if it sits next to the exe, one level up, or at `Documents\Granite WMS\Granite V6.0`; otherwise pick the folder or zip on Step 1.

## What each step does

1. **Release and Install Folder**: the Granite release as a folder or a `.zip` (checked for the four app folders, the create script and `GraniteScaffold\Prerequisites`; a zip is extracted to `C:\ProgramData\Granite Install Wizard\Releases` and reused next time), the install folder (default `C:\Program Files\GraniteWMS`, created on the spot if it doesn't exist), company name, date format. "Load saved profile" pre-fills every answer except passwords.
2. **Prerequisites**: what the server already has (IIS features via DISM, URL Rewrite, ASP.NET Core 8 and 6 Hosting Bundles), and whether to install what's missing. Everything comes from the release's own `GraniteScaffold\Prerequisites`, so no internet is needed.
3. **SQL Server**: instance (detected locally and on the network, same code as the BI wizard), how to connect (Windows or a SQL login; needs sysadmin), whether to **create a new, clean database** or **use an existing Granite database** (listed after Test Connection), and the SQL login the apps will use. Hotfix database scripts and Hotfix app files are separate options. **Test Connection must pass for the settings currently entered** before Next is allowed. It reports sysadmin, Windows-only authentication mode, an existing database, and whether an existing app login's password matches.
4. **Websites**: the address users and scanners will type, and each component's IIS site/app pool name and HTTPS port (defaults 40099 Web Desktop, 40081 Business API, 40082 Custodian, 40080 Process App). Business API is always installed. **Replace existing IIS sites** (off by default, confirmed, never saved in a profile) reinstalls over sites that already have these names.
5. **HTTPS Certificate**: create a self-signed certificate (every host name, IPv4 address and localhost as SANs, 5 years), or pick an existing one from Local Computer\Personal.
6. **Review and Install**: a summary, a **Dry run** option, Save profile, and Start Install with a stage list and live log.

## What "Start Install" does

Ten stages, stopping at the first failure. With **Dry run** ticked, every read-only check runs for real (SQL connection and rights, existing database/login/sites/ports, parsing the create script) and every change is logged as "Would ..." without being made.

1. **Pre-flight**: collects every problem and reports them all at once, before anything changes: not elevated, incomplete release, Business API off, duplicate ports, SQL not reachable / not sysadmin / Windows-only auth, database exists without "Drop and recreate", existing app login with the wrong password, create script won't parse, IIS site name or port already taken, port used by another program, chosen certificate gone or without a private key.
2. **Prerequisites**: missing IIS features in one DISM call, then URL Rewrite, then the Hosting Bundle (in that order: the bundle only registers its IIS module if IIS is already there), then restarts WAS/W3SVC.
3. **Certificate**: creates and stores it (key persisted in the machine store), exports a `.cer` to `<install>\Certificates` for scanners and PCs, optionally trusts it on the server.
4. **Database**: creates the app login if missing; in "new" mode runs `GraniteDatabase_Create.sql` (in "existing" mode it doesn't touch the database's contents); maps the login into the database as db_owner; then, if chosen, runs `Hotfix\Database\*.sql` and the SQL inside `Hotfix\*.md`.
5. **Copy files**: if replacing, the old sites with these names are removed first (site, app pool, http.sys SSL binding, the wizard's firewall rules for their old ports), since their worker processes hold files open; then each component to `<install>\<ReleaseFolder>`, Hotfix binaries on top if chosen (never their appsettings/web.config/nlog.config), Zone.Identifier removed. A non-empty target folder is renamed to `.bak-<timestamp>` first (retried for about 20 seconds while a just-stopped worker process lets go of its files).
6. **Configure**: rewrites each `appsettings.json` (original kept as `appsettings.json.orig`).
7. **IIS**: app pool (No Managed Code, AlwaysRunning, idle timeout off), HTTPS site, http.sys certificate binding, Modify rights for the pool identity for every site first; then each pool is recycled once and each site started.
8. **Firewall**: one inbound TCP rule per port, named `Granite WMS - <component> (<port>)`.
9. **Verify**: the app login reads `dbo.SystemSettings`; each site answers over HTTPS on localhost (Web Desktop specifically serves its `appsettings.json`). A 5xx or no answer is retried every 10 seconds for about a minute while the app starts.
10. **Summary**: `InstallSummary.txt` and `InstallProfile.json` (no passwords) in the install folder.

Every run also writes a full log to `C:\ProgramData\Granite Install Wizard\Logs\`, passwords masked.

## Deviations and decisions

**IIS through appcmd.exe and netsh, not Microsoft.Web.Administration.** The v0.1.0 csproj TODO planned a `<Reference>` to `%windir%\system32\inetsrv\Microsoft.Web.Administration.dll`. That DLL only exists once IIS is installed, and on a fresh server the wizard installs IIS itself two stages earlier; it isn't a supported NuGet package either, so it would have to be bundled and version-matched by hand. `appcmd.exe` ships with every IIS. The certificate binding goes through `netsh http add sslcert`, the standard way (appcmd can't set it). Every command is built as an argument list in `Core/IisCommands.cs`, which the harness checks without IIS.

**Own SQLCMD parser, keeping comments.** The BI wizard's `ScriptBatchParser` refuses `:setvar`, but `GraniteDatabase_Create.sql` is SSDT-generated and depends on it, so `Core/GraniteSqlScriptParser.cs` honours `:setvar` and `:on error` (caller values win) and still refuses `:r`. It keeps the BI parser's protection against `GO` inside comments (the BI project's commented-out `CREATE LOGIN` incident) by finding separators on a comment-stripped copy, but cuts batches from the original text at the same offsets, so the hundreds of Granite views and procedures keep their comments in `sp_helptext`. No sqlcmd.exe or SqlServer PowerShell module is needed on the server. The create script yields 553 batches: 557 GO-separated segments minus 4 that are only comments or directives.

**Connection strings written by hand, with classic keywords.** `Microsoft.Data.SqlClient`'s builder writes `Trust Server Certificate=True` (with spaces). The Business API folder also ships `System.Data.SqlClient.dll` and ServiceStack OrmLite, and `System.Data.SqlClient` throws "Keyword not supported" on the spaced form. `Core/ConnectionStringFormatter.cs` writes `TrustServerCertificate=True`, as the release's own files do, and quotes values so passwords with `;` or quotes survive (harness-checked).

**Bug caught by the harness.** `AppSettingsWriter` first crashed with "JsonSerializerOptions instance must specify a TypeInfoResolver": in .NET 8, custom serializer options used on a `JsonNode` with added values need an explicit resolver. That would have stopped every real install at stage 6. Fixed, and the harness now covers it against all four real files.

**The GRANITE user clash.** The create script adds a database user named `GRANITE` for `BUILTIN\Users`. User names are case-insensitive, so an app login named `Granite` can't have a user of the same name. The wizard looks the user up by the login's SID and creates `Granite_App` if the name is taken.

**Existing databases and logins.** In "create new" mode the create script drops its database if it exists, so the wizard refuses to run over one unless "Drop and recreate" is ticked and confirmed (and profiles never save that flag); the refusal message points at "use an existing database" instead. In "use existing" mode the database must exist and have Granite's `SystemSettings`, `Process` and `MasterItem` tables, and the drop option can't be set. An existing app login is reused only if the entered password works. The wizard changes an existing login's password only when "change its password" is ticked on Step 3 (confirmed in a dialog, never saved in a profile), since another app may share the login.

**CORS origins.** Business API allows Web Desktop and Process App, Custodian allows Web Desktop, each by every host name on the certificate plus the Step 4 address and localhost. A browser sends whatever origin the user typed (IP on a scanner, hostname on a PC), so one origin alone breaks the others.

**Date format pairing.** The Business API's shipped appsettings says its `DateTimeFormat` must match Web Desktop's, in .NET spelling (`dd'/'MM'/'yyyy` for Web Desktop's `DD/MM/YYYY`). One choice on Step 1 writes both.

**Custodian's `Granite_Test` connection** ships pointing at a developer's `.\SQL2022` with their password. The catalog name is kept (nothing says whether Custodian uses it) but server and login are replaced so no developer credentials stay on a client server.

**Default ports.** Web Desktop on 40099 is the standard across Granite installs. Process App (the scanner app) on 40080 is what the first real install used. Business API 40081 and Custodian 40082 are this wizard's choice. The release's own sample files mention 40081, 40086, 40096, 5001 and 6001 in different places, so they couldn't be used as a source.

## Confirm on the next run

Confirmed by the v0.2.0 run: DISM check and feature install, Hosting Bundle install and IIS restart, self-signed certificate binding in http.sys (sites served HTTPS), the full create script including SQLCLR, the Hotfix scripts, the `GRANITE` / `Granite_App` user fallback, Process App starting without a reboot, appcmd and netsh commands, firewall rules.

Still to confirm:
- The verify stage now reports all four sites healthy (v0.2.1).
- App pools show ".NET CLR version: No Managed Code" in IIS Manager (the empty `/managedRuntimeVersion:` argument).
- Web Desktop sign-in end to end.
- "Use an existing Granite database": the dropdown lists the server's Granite databases after Test Connection, the install leaves the data alone, and the apps work against it.
- Step 3's result lines each show in their own colour, and the hotfix-scripts box stays off in "existing" mode after Back/Next.
- "Change its password" on a re-install: the login's password is changed and the apps sign in with it.
- "Replace existing IIS sites" on Ultra: the four old sites and pools go, their folders become `.bak-*`, the stale `Granite WMS - Process App (40080)` and `Web Desktop (40099)` firewall rules are removed, and the new sites come up on the Step 4 ports.
- V7.0: pick `Granite V7.0.zip` on Step 1 (unpacks in a minute or two), then a dry run, then a real install into a new database name. The create script has only been parsed, not run, for V7.0.

## Verified: `LogicHarness/`

A small console project linking the real engine files from `GraniteInstallWizard/` (no copies), leaving out everything that needs Windows, IIS, SQL Server or WinForms. Run it against a release folder:

```
cd LogicHarness
dotnet run -c Release -- "C:\Users\izakm\Documents\Granite WMS\Granite V6.0"
dotnet run -c Release -- "C:\Users\izakm\Documents\Granite WMS\Granite V7.0.zip"
```

A zip, or a V7 folder whose apps are still inner zips, is unpacked first with the wizard's own `ReleaseSource`, so the unpacking is tested on the real release too.

It checks, against the real files: the create script's batches, variables, directives, comments and line numbers (CRLF and LF); the UTF-16 SQLCLR hotfix; the CREATE OR ALTER rewrite; the Custodian.md SQL; the Custodian token (bundled and release copies parsed, bad files refused, release copy preferred, the upsert parameterised); installing alongside (block-shifted port suggestions, fallbacks, Windows reserved ranges and their netsh output, replaced sites freeing ports, site-name suffixes, the profile flag); GO-in-comment and `:r` edge cases; all four `appsettings.json` rewrites (connection strings round-tripped through SqlClient, CORS origins, date formats, licence and telemetry kept, Custodian's test connection, Custodian switched off); password quoting; every appcmd/netsh/icacls argument list and appcmd XML parsing (IPv6 bindings included); DISM table parsing; profile round-trip (no passwords, never the drop flag); hotfix exclusions; the new-vs-existing database rules; the hotfix-scripts defaults; and the login-password reset messages and profile handling; and the reinstall site/port conflict rules, replayed against Ultra's actual sites and ports; and release zips (wrapped top-level folder, reuse, zip slip, no release, ambiguous folders); and V7-style releases (inner zips unpacked or left alone, the user's folder untouched, V7 folder spellings, Hotfix\Business Api, loose V7 SQL not treated as hotfix scripts, Process App's new DateTimeFormat). **165 checks against V6.0, 158 against V7.0, all passing.**

It does not verify SQL execution, DISM, installers, certificates, IIS, firewall or the GUI.


## History

### 2026-10-06: install alongside an existing install (v0.7.0)

Asked for while installing V7 next to V6 on Ultra: every name, folder, database and port had to be changed by hand, and the safe port range looked up. Step 4 now has "Install alongside an existing Granite install". Ticking it scans the server, gives the sites a suffix (`PortPlanner.SuggestNameSuffix`: " V7" from a release named "Granite V7.0", otherwise " 2", " 3"...), picks free ports (`PortPlanner.Suggest`) and switches "Replace existing IIS sites" off and disables it, since the point is to leave the existing install alone. "Find free ports" does only the ports. Step 4 checks the ports as soon as it opens and says what holds any that are taken; nothing is changed until asked.

- **What counts as taken** (`PortUse`): a port bound by an IIS site that's being kept, a port anything else listens on, and anything in a Windows excluded port range. Hyper-V, WSL and Docker reserve blocks that move between reboots and often land in the 40000s; IIS can't bind inside them and the error at install time is unhelpful, so they're read from `netsh interface ipv4 show excludedportrange protocol=tcp` (netsh started by full path). Ports of sites being replaced count as free.
- **Suggestions** stay in 1024-49151 (below is for system services; 49152 and up is Windows' range for outgoing connections). The default block is tried shifted by 0, 100, ... 900, so V6 on 40080-40099 gives V7 40180-40199; only if no shifted block is wholly free is each port picked separately, counting up from its default.
- **Pre-flight** now also errors on a port in a reserved range, and, when alongside is ticked, on an install folder that already holds files. Without that, an alongside install pointed at the existing install folder would move the running install's folders to `.bak` (the existing behaviour for reinstalls, which stays a warning when alongside is off).
- The folder and database names are still chosen on Steps 1 and 3; pre-flight catches a clash for both (the database one already existed).

### 2026-10-05: Custodian token set on every install (v0.6.0)

Setting up Custodian by hand at a client (Newburg) needed two things the release doesn't do for you: the connection string named `CONNECTION` (which this wizard already writes) and the token from `Custodian.md`. The wizard only ran `Custodian.md` as part of the V6.0 Hotfix database scripts, which are off by default against an existing database, and V7.0 has no `Custodian.md`. So a V7.0 install, or any install into an existing database, could leave Custodian without its token.

Now `CustodianToken` handles it as its own step, after the Hotfix scripts, whenever Custodian is being installed:
- **Source:** a Custodian.md picked on Step 3 if there is one (it overwrites the database's token, since picking one means replacing a token GitHub refuses), otherwise the release's `Hotfix\Custodian.md` (only fills a missing token in an existing database). Nothing is bundled. An earlier draft embedded `Resources\Custodian.md` in the exe; it was dropped before release because the token is a live credential for a shared GitHub repository: compiled in, it would ship to every server, couldn't be rotated without a rebuild, and goes stale (on Ultra, 7 October, GitHub refused the V6.0 release's Version 6 token with "Bad credentials"; the Version 7 token from Granite fixed it). With no file, the install completes, the log says so, and verification reports the token missing.
- **Checked after install:** Custodian returns HTTP 200 with an empty process catalogue when GitHub refuses its token, and logs nothing. The only place the cause shows is the `StoreConnection` message in its `/config` reply, so verification and the dashboard read that (`CustodianRepository` in the shared core) and say "token rejected" with what to do.
- **Not run as SQL:** the token, `EncryptionKey` and `Version` are read out of the file and checked (base64, lengths, a version number). A file that doesn't match stops the install before the database is touched. They're then written by one parameterised batch.
- **Insert or update:** the file's own SQL is an `UPDATE` that silently does nothing when there's no Token row. The new batch inserts the row (as `Granite.Custodian`, with the column list Custodian's own inserts use) when there is none under either `Granite.Custodian` or the older `GRANITECUSTODIAN`, and otherwise updates the existing row(s). Both spellings are updated rather than adding a second row, because Custodian renames `GRANITECUSTODIAN` rows on start-up and would end up with duplicates.
- **New vs existing database:** a new database always gets the token. An existing one only gets it where the value is empty, so a working token on a live database is never replaced.
- Dry run logs what it would do; Step 6 shows the source; verification checks a non-empty token exists (with the app login) and fails the run if not.

### 2026-09-29: Granite V6.0.zip refused as unsafe (v0.5.1)

Picking `Granite V6.0.zip` on Step 1 failed with "The zip contains an unsafe path and was not extracted: /". That zip stores its root folder as an entry named just `/`. `Path.Combine` treats `/` as rooted, so it resolved to `C:\` and the zip-slip check refused the whole zip. Entry names now have leading slashes stripped (zip entries are always relative) and an empty name is skipped; anything that still resolves outside the extraction folder (`..\`, a drive letter) is refused as before. Checked every entry in both real zips: the `/` in V6.0 is the only unusual one. Two new harness checks build a zip the same way.

### 2026-09-29: V7.0 releases (v0.5.0)

Izak asked whether the wizard works for V7. Compared against the real `Granite V7.0.zip`:

- **Same:** .NET 8 on all four apps, the same prerequisites (Hosting Bundle 8.0.30 instead of 8.0.18, found by the existing `dotnet-hosting-8.*` pattern), the same SQLCMD variables and `GRANITE` user in the create script (591 batches, none unresolved), the same appsettings keys the wizard writes, and Web Desktop's `web.config` with its URL Rewrite rule.
- **Packaging:** V7.0 ships each app as a zip inside the release (`GraniteBusinessApi.zip`, `GraniteDatabase\GraniteDatabase.zip`, `GraniteScaffold.zip`, `Hotfix\ProcessApp.zip`). `ReleaseSource` now unpacks the inner zips the core stack needs next to where they sit, which gives the V6.0 layout. Telemetry (470 MB), Scheduler, integrations, label printing and the ERP database packs are skipped. A V7.0 zip that was unzipped by hand is unpacked into `C:\ProgramData\Granite Install Wizard\Releases` as well, leaving the original folder alone.
- **Spelling:** `GraniteBusinessApi` / `GraniteWebDesktop` (V6.0: `GraniteBusinessAPI` / `GraniteWebdesktop`) and `Hotfix\Business Api` (V6.0: `Hotfix\BusinessAPI`). `ReleaseLayout` matches folder names ignoring case and spaces. Installed folders keep the V6.0 names, so a reinstall lands in the same place.
- **Hotfix database scripts:** V7.0 has no `Hotfix\Database` or `Custodian.md`. Its `Hotfix\SQLCLR_Install.sql` sits loose in the Hotfix root and holds a different build of the Granite SQLCLR assembly (dated 3 August) from the one already in V7.0's create script and `GraniteSQLCLR.zip` (31 August). Running it after the create script could put the older assembly back, so loose SQL is not picked up. `GraniteDatabase\hotfix\Integration_Accpac_MasterItem.sql` is an Accpac view (needs `$(AccpacDatabase)`), not core. Step 3 now names the scripts the release actually has and disables the box when there are none.
- **`GraniteDatabase\7.2 Upgrade.sql`:** everything in it (dimension columns to DECIMAL(19,4), `Transaction.ToTrackingEntity_id`, the Repalletize permission) is already in V7.0's create script, so a new database doesn't need it. It isn't safe to re-run (plain `ALTER TABLE ... ADD`) and the wizard doesn't run it; upgrading an existing V6.0 database is out of scope, as before.
- **appsettings:** V7.0 adds `DateTimeFormat` to Process App (a .NET format like the Business API's), now written from Step 1's date format when the key exists. New keys the wizard doesn't touch are kept as shipped: `Auth.SessionExpiryMinutes`, `SessionIdleTimeoutMinutes`, `AuthCookieExpireMinutes`, `MenuLayout`, `ScaleServices`.
- **Hotfix app files:** Step 3 and the review now list which apps have Hotfix files. V7.0: Business API (a newer ServiceInterface dll, 15 September) and Process App (the same build as the release, harmless).

### 2026-09-29: release zips, and Program Files as the default (v0.4.0)

Step 1 now takes the Granite release as a folder or as the `.zip` it ships in. A zip is extracted to `C:\ProgramData\Granite Install Wizard\Releases\<zip name>` with a progress line; a marker file records the zip's size and timestamp, so picking the same zip again reuses the extraction, and a changed zip is extracted fresh. The release can sit up to two folders deep (zips usually wrap everything in "Granite V7.0\..."), and two releases side by side are reported as ambiguous rather than guessed. Entries that would land outside the extraction folder (zip slip) stop the extraction.

The default install folder is now `C:\Program Files\GraniteWMS` (Izak's standard). Step 1 shows whether it exists, has a "Create folder" button, and offers to create it on Next if it's still missing. The user-facing "V6.0" wording is gone (Step 1 hint, success message, summary header), ready for the V7 comparison.

### 2026-09-29: standard ports (v0.3.3)

Web Desktop's default port is now 40099, the standard across Granite installs, and Process App (the scanner app) defaults to 40080, as on the first install. Business API and Custodian stay on 40081 and 40082. Names still follow the release folders, so the app in `GraniteProcessApp` stays "Process App".

With these defaults a reinstall on Ultra lines up exactly with the old sites: every port is held by the old site of the same name. Pre-flight no longer reports a separate port error in that case, because the site-name message already covers it (and "Replace existing IIS sites" frees the port). With Replace off that's four problems, one per site, instead of eight. A port held by a site with a different name is still reported.

### 2026-09-29: reinstalling over an earlier install (v0.3.2)

A v0.3.1 dry run on Ultra, where the first install's four sites still exist, stopped at pre-flight with seven problems: all four site names taken, and three ports held by the old sites (the first install had put Web Desktop on 40099 and Process App on 40080, so the Step 4 defaults collided with it). Pre-flight was right to stop, since the wizard was fresh-install only, but that left no way to reinstall except deleting the sites by hand.

New opt-in on Step 4: **Replace existing IIS sites with these names**. Off by default, confirmed in a dialog, never saved in a profile. When ticked, a site with the same name is a warning, not a blocker, and so is any port that site holds; a port held by a differently-named site or by a non-IIS program still blocks. The old sites are removed at the start of the copy stage, not the IIS stage: their app pools hold files open in the folders the copy stage renames to `.bak`. Removal covers the site, its same-named app pool, the http.sys SSL binding on each of its https ports, and this wizard's firewall rule for each old port. The `.bak` rename now retries for about 20 seconds while worker processes exit. The rules live in `Core/SiteConflictCheck.cs`, and the harness replays Ultra's exact sites and ports: seven errors with Replace off, none with it on.

Also from that screenshot: the log console now wraps long lines (the important half of each pre-flight message was off the right edge), and a dry run that stops at pre-flight says "The dry run found problems ... Nothing was changed." instead of the generic error.

### 2026-09-28: Step 3 feedback from the first existing-database try (v0.3.1)

A screenshot of v0.3.0's Step 3 in "use existing" mode against the first install's database showed the checks working (database found and recognised, existing `Granite` login rejected because its password was different) but three problems:

1. **Everything was red.** The result was one label coloured by its worst line, so "Connected", "data is kept" and the hotfix note all turned red along with the one real problem. Each line now has its own label and colour: green for OK, orange for warnings, red for blockers.
2. **"Run the Hotfix database scripts" was ticked in existing mode.** v0.3.0 stored a plain bool defaulting to on; `OnEnter` set the mode radio (which set the box's default to off) and then copied the stored `true` back over it, so any re-entry of Step 3 in "existing" mode turned the scripts on. The context now keeps `DatabaseHotfixChoice` (null until the user ticks or unticks the box) and derives `ApplyDatabaseHotfix` from it or from the mode. Deliberately switching mode clears the choice; loading the page doesn't.
3. **No way forward on a re-install.** The first install created the `Granite` login with a password that wasn't written down, and the wizard refuses to guess or overwrite it. New opt-in: "If this login already exists with a different password, change its password to this one", confirmed in a dialog, never saved in a profile (like "Drop and recreate"), and only acted on if the entered password really doesn't work (`ALTER LOGIN ... WITH PASSWORD`, built with QUOTENAME).

### 2026-09-28: use an existing database (v0.3.0)

Asked for after the first run: point a new install at a Granite database that already exists (a restored backup, a copy of live, a database moved from another server) as well as creating a clean one. Step 3 now offers both. "Use an existing Granite database" never runs the create script and never drops anything; it checks the database really is Granite (`dbo.SystemSettings`, `dbo.Process`, `dbo.MasterItem`, checked with three-part `OBJECT_ID` names so the database name is only ever a parameter), maps the app login in as db_owner, and configures the apps against it. After Test Connection the database box lists every Granite database the connecting account can open.

The old single "Apply Hotfix" option is now two: Hotfix database scripts and Hotfix app files. Against an existing database the scripts change live objects (replace `API_QueryDocumentProgress`, reinstall the SQLCLR assembly, overwrite the Custodian token), so switching to "use existing" turns the scripts option off by default, and pre-flight warns to take a backup if it's turned back on. Profiles save the mode and both choices. Covered by 14 new harness checks (both modes' blocking and warning rules, profile round-trip).

### 2026-09-28: first real run, and two healthy sites reported as HTTP 503 (v0.2.1)

v0.2.0's first real install completed every stage, but the verify stage reported Business API and Custodian as HTTP 503. Their own logs said otherwise: the Business API's log shows "Application started" and "Application is shutting down" in the same millisecond (16:21:26), while later sites were still being written to applicationHost.config, and Custodian's log shows it serving real requests at 16:21:53 and 16:23:07, after the 503 at 16:21:31. Two causes, both in the wizard: each site was started straight after its own configuration, so an AlwaysRunning pool was already running when the remaining `appcmd` writes caused a restart; and the verify stage asked each site exactly once, about 4 seconds after start-up.

Fixed by configuring every pool and site first, then recycling each pool once and starting each site; and by retrying a 5xx or no answer every 10 seconds for about a minute before calling it a failure. The failure message now points at the `Granite*.log` files the apps actually write in their own folders (not a `logs` subfolder) and the IIS AspNetCore Module V2 event log source.

The same Custodian log shows `SSRSWebServiceUrl` is empty in SystemSettings, so reporting calls fail until SSRS is configured. That's outside the core-stack scope and expected on a fresh install.

### 2026-09-28: from prototype to the C# app (v0.2.0)

The v0.1.0 plan was to record what a successful Scaffold run does before building anything. Instead, the spec was worked out from the V6.0 release files directly (runtimeconfig files, appsettings, the create script, the Hotfix folder, Scaffold's own PowerShell scripts), first as a PowerShell prototype (deleted on 2026-09-29, never committed), then ported here so the tool matches the BI Deployment Wizard. The C# version adds, over the prototype: comment-aware batch splitting, hand-built connection strings with classic keywords, IIS via appcmd, reading SANs from existing certificates, Test Connection gating Step 3, and the LogicHarness.

What Scaffold did after the 2026-08-28 DLL fix, from its log: it got past startup, then stopped on `Login failed for user 'ULTRA\izakm'` and `The UPDATE permission was denied on the object 'SystemSettings', database 'GraniteLive'`. Pre-flight here checks both kinds of problem (sysadmin, SQL login rights) before anything runs.

### 2026-08-28: why this exists

While working through the GraniteWMS BI Deployment Wizard project (see the
sibling `Granite BI Deploy` repo), testing Granite Scheduler required a full
local Granite install with IIS. Running the vendor's own
`GraniteScaffold\Granite.Scaffolding.exe` hit two real packaging bugs back
to back, both confirmed against evidence rather than guessed:

1. `System.IO.FileNotFoundException` for `System.Numerics.Vectors,
   Version=4.1.4.0` -- the DLL simply wasn't shipped in the
   `GraniteScaffold` folder. A `Granite.Scaffolding.log` entry from
   `2026-01-27` shows the identical crash from an earlier attempt, so this
   isn't specific to this machine.
2. After copying in a same-named DLL found elsewhere in the install package
   (`GraniteLabelPrintingBartender\*\System.Numerics.Vectors.dll`, all 12
   copies byte-identical), the error changed to
   `System.IO.FileLoadException`: "the located assembly's manifest
   definition does not match the assembly reference" -- that copy is
   version `4.1.3.0`, not the `4.1.4.0` the app references. Fixed with a
   binding redirect in `Granite.Scaffolding.exe.config`, mirroring a
   redirect the file already had for a different assembly
   (`System.Reflection.TypeExtensions`) -- so this is clearly a known
   pattern for the Cradle team, just missing for this one dependency.

Both fixes were applied directly to the shipped files at
`C:\Users\izakm\Documents\Granite WMS\Granite V6.0\GraniteScaffold\` on
Izak's machine, not committed anywhere -- that's Cradle's vendor package,
not something this repo owns a copy of.

## Files

```
GraniteInstallWizard/
  GraniteInstallWizard.csproj     net8.0-windows WinForms, Microsoft.Data.SqlClient 5.2.2
  app.manifest                    requireAdministrator
  Program.cs
  MainForm.cs                     wizard shell: header, step dots, Back/Next, release auto-detect
  Models/
    InstallContext.cs             all wizard state, shared across steps
    GraniteComponent.cs           the four core components, folders, default sites/ports
    LogEntry.cs                   one log line (Info/Success/Warning/Error/Detail/DryRun/Stage)
  Core/
    InstallRunner.cs              the ten stages, dry run, log file, summary
    ReleaseFolderCheck.cs         is this folder really a Granite release
    ReleaseSource.cs              release folder or .zip: find the release root, extract and reuse zips, unpack V7 inner zips
    ReleaseLayout.cs              folder lookups that ignore case and spacing (V6.0 vs V7.0 names)
    HotfixScripts.cs              which Hotfix database scripts a release has
    GraniteSqlScriptParser.cs     SQLCMD-mode parsing, CREATE OR ALTER rewrite, markdown SQL
    DatabaseInstaller.cs          login, create script, db_owner mapping, hotfix scripts
    SqlServerInspector.cs         Test Connection / pre-flight SQL checks
    SiteConflictCheck.cs          pre-flight IIS site and port rules, incl. "Replace existing IIS sites"
    SqlConnectionFactory.cs       the wizard's own SQL connections
    SqlInstanceDiscovery.cs       local + network instance discovery (from the BI wizard)
    ConnectionStringFormatter.cs  connection strings written into appsettings.json
    AppSettingsWriter.cs          per-component appsettings.json rewrite
    DateFormatConverter.cs        DD/MM/YYYY -> dd'/'MM'/'yyyy
    PrerequisiteService.cs        DISM, URL Rewrite, Hosting Bundle detection and install
    WindowsFeatureList.cs         required IIS features, DISM table parsing
    CertificateService.cs         self-signed creation, existing certificate listing
    FileDeployer.cs               copy, hotfix overlay, unblock
    IisCommands.cs                appcmd/netsh/icacls argument lists, appcmd XML parsing
    IisService.cs                 runs those commands
    VerificationService.cs        post-install database and HTTPS checks
    LocalAddressDiscovery.cs      host names, IPv4 addresses, listening ports
    InstallProfile.cs             saved answers (no passwords)
    ProcessRunner.cs              hidden external process with captured output
  UI/
    WizardStepControl.cs          base class for the six steps
    Step1ReleaseControl.cs
    Step2PrerequisitesControl.cs
    Step3SqlServerControl.cs
    Step4WebsitesControl.cs
    Step5CertificateControl.cs
    Step6InstallControl.cs        review, dry run, install console

LogicHarness/                     engine checks against the real release (see above)
```
