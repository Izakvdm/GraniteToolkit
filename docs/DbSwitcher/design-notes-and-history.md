# GraniteWMS DB Switcher

Points an installed GraniteWMS stack at a different database on the same SQL Server, so client databases can be tested locally without an IIS install per client.

One screen: pick the install, pick a database, **Switch** (or double-click the database). A switch takes a few seconds.

## What a switch does

1. **Fixes the apps' login access** (optional, on by default). A restored client backup usually has an orphaned user for the apps' SQL login. In order of preference: nothing to do if the login is sysadmin or already mapped; re-link an orphaned SQL user with the login's name (`ALTER USER ... WITH LOGIN`); otherwise create a user for the login (named `<login>_DbSwitcher` if the login's name is taken by another user, e.g. the create script's `GRANITE` user). The user is then made `db_owner`, same as the Granite create script does.
2. **Checks the apps' own login can read the database** (`SELECT COUNT(*) FROM dbo.SystemSettings`) before any file is touched.
3. **Rewrites the connection strings** in the three apps that have one. Only the database name changes; every other part of the string and the file (comments, key order, line endings, BOM, pool settings, the classic `TrustServerCertificate` keyword) is left byte-for-byte as it was. The first time a file is changed, the original is saved next to it as `appsettings.json.before-dbswitcher`. If any write fails, the files already changed are put back.
4. **Recycles the app pools** (or starts a stopped one).
5. **Waits for the apps to answer** (anything but a 5xx within a minute) and reports each one.

| App | Setting changed |
|---|---|
| Business API | `ConnectionStrings:CONNECTION` |
| Custodian | `ConnectionStrings:CONNECTION` (its `Granite_Test` entry is left alone) |
| Process App | `ConnectionStrings:ConnectionString` |
| Web Desktop | nothing (it has no database connection; it goes through the Business API and Custodian) |

After a switch, sign in to Web Desktop again: the browser's session belongs to the previous database.

## How installs are found

From IIS (`appcmd list site/app/vdir`), by what's in each site's root folder rather than by site name: `Granite.Business.API.dll`, `Granite.Custodian.dll`, `Granite.Process.App.dll`, or for Web Desktop `index.html` next to an `appsettings.json` with `Business_API_Endpoint`. Sites whose folders share a parent folder are one install, so a V6 install in `C:\Program Files\GraniteWMS` and a V7 install in another folder show up as two installs, each switched separately.

The install's version is the FileVersion of `Granite.Business.API.dll` (6.0.0.0 in V6.0, 7.2.0.0 in V7.0).

## How a database's version is worked out

Checked against the V6.0 and V7.0 release scripts on 2026-09-30:

- `SystemSettings` Granite / `DatabaseVersion` is **not** usable on its own: both create scripts write `6.0.0.0`, and only when the row is missing.
- `dbo.Migration` is the marker used: V6.0's create script adds `SCHEMA_500` to `SCHEMA_600`, V7.0 adds `SCHEMA_700` on top. The highest `SCHEMA_nnn` gives the version (700 is 7.0).
- Fallbacks: `dbo.ProcessFunction` exists (V7 only), then the `DatabaseVersion` setting.

Only the major version is compared with the install (a V7.2 Business API runs on a `SCHEMA_700` database). A mismatch or unknown version asks before switching. Databases without `SystemSettings` are treated as non-Granite and hidden unless "Show non-Granite databases" is ticked.

It also warns when the database has SQLCLR assemblies but `clr enabled` is off on the server (V7's `clr_` procedures would fail).

## Running two versions side by side

Both V6.0 and V7.0 target .NET 8, so one Hosting Bundle (the newer 8.0.30 from V7) serves both. Each version needs its own folder, sites, app pools and ports (e.g. 400xx for V6, 410xx for V7), and its Web Desktop and Process App appsettings must point at its own Business API and Custodian ports. The install wizard can do the second install on alternate ports.

Browsers don't separate cookies by port, so signing in to both on `localhost` in one browser can clash. Use separate browser profiles, or a host name per version (`v6.localhost`, `v7.localhost`) as a binding host header. The switcher uses the host header in its "Open" buttons when one is set.

## Not in scope (v0.1)

- Changing the SQL Server itself (only the database name changes). If the apps point at different servers, the switcher says so.
- Integrations, Scheduler, Label Printing, Telemetry: left pointing wherever they were.
- Neutralising client settings in restored databases (ERP endpoints, printers, email).

## Files

- Settings (no passwords): `C:\ProgramData\Granite DB Switcher\settings.json`
- Logs: `C:\ProgramData\Granite DB Switcher\Logs\switch-yyyyMMdd.log`

## Building

- `Build-And-Run.cmd`: builds (Release) and starts it. It asks for administrator rights (IIS and `C:\Program Files` need them).
- `Publish.cmd`: self-contained single-file exe to `dist\GraniteDbSwitcher-v<version>.exe`.

Same structure as the install wizard: `Models/`, `Core/` (pure logic plus the Windows, IIS and SQL services), `MainForm.cs`. .NET 8 WinForms, `Microsoft.Data.SqlClient` 5.2.2, IIS through `appcmd.exe`.

## LogicHarness

`LogicHarness/` is a net8.0 console that links the real pure source files (connection string and appsettings editing, version rules, appcmd parsing, install discovery) and checks them without Windows, IIS or SQL Server:

```
dotnet run --project LogicHarness -c Release -- <folder of real appsettings files>
```

The folder holds copies of release `appsettings.json` files named `v6-GraniteBusinessAPI.json`, `v7-GraniteCustodian.json` and so on. They're not kept in the repo because they carry the shipped passwords and ServiceStack licence. Without a folder, only the built-in cases run.

v0.1.0: 428 checks passing, including the V6.0 and V7.0 release files for all four apps.

## History

- **v0.1.0** (2026-09-30): first version. Not yet run on Windows.
