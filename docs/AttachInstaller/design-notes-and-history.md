# Granite Attach Installer (GUI)

A proper installer, built the same way as the GraniteWMS Install Wizard
(WinForms, appcmd.exe-driven IIS management, elevation via app.manifest):
pick the SQL Server instance and database, pick the IIS site/app pool and
port, optionally name the process that will call the attach step, and it
publishes the app, sets up the database objects, and wires up IIS in one
run.

**Status: written Sept 29, 2026. Not yet built or run - I can't compile or
test a Windows Forms app from this session (no Windows GUI runtime here).
Build it on your machine (Visual Studio, or `dotnet build`) and try it;
if anything doesn't compile or misbehaves, tell me the exact error and I'll
fix it fast - I'd rather iterate on a real error than guess further blind.**

## What it does, step by step

1. **SQL Server & database** - pick or type a SQL Server instance (the
   dropdown lists local instances immediately and network ones a moment
   later, same discovery the GraniteWMS Install Wizard uses), Windows or SQL
   auth, and a database name (type it, or click "List databases"). Test
   Connection before moving on.
2. **App, IIS site, and process** - the GraniteAttach.csproj to publish
   (auto-detected if this installer is still inside the Granite Attach
   folder), the IIS site name/app pool/port/physical path, an optional
   process name (if you already know what you'll call the Granite process
   that uses the attach step), and the public base URL (auto-filled from
   this machine's LAN IP).
3. **Review and install** - one Install button runs everything: creates the
   three storage tables, resolves and creates the process-step kit's three
   stored procedures for your process name (if given) plus a ready-to-paste
   WebTemplate file, publishes the app (`dotnet publish -c Release`) to the
   physical path, writes `appsettings.Production.json` (generating the
   encryption key/capture-link secret on first install, keeping them on a
   reinstall), and creates/updates the IIS app pool and site (removing and
   recreating the site if one with that name already exists, so the
   settings just entered take effect), grants the app pool write access to
   its folder, opens the firewall, and starts it. The log shows exactly what
   ran, and finishes with the Process Designer step table to build next.

## Prerequisites this installer does NOT check for you

- **IIS must already be installed**, with the ASP.NET Core Hosting Bundle
  (this registers the ASP.NET Core Module IIS needs to run a .NET app - it's
  a separate install from the .NET SDK). If the GraniteWMS Install Wizard
  set this machine up already, it's almost certainly there already, since
  Web Desktop/Business API need the same thing. If the site comes up with a
  500.19 or 502.5 error after installing, this is the first thing to check
  - install the Hosting Bundle from
  `https://dotnet.microsoft.com/download/dotnet` (matching this app's .NET
  10 target) and run `iisreset`.
- **.NET 10 SDK** on whichever machine builds/publishes the app (this
  installer calls `dotnet publish`, so it needs `dotnet` on PATH).
- **Run the installer as Administrator** - it needs to create IIS sites/app
  pools and write under wherever you point the physical path.

## Building it

Open `GraniteAttachInstaller.csproj` in Visual Studio and build/run, or from
this folder:

```powershell
dotnet build
dotnet run
```

(or publish it as a standalone exe with `dotnet publish -c Release` and run
that .exe directly, elevated).

## How this differs from the `deploy/` PowerShell scripts

`../deploy/install-database.ps1` and `../deploy/install.ps1` do the same two
jobs (database setup, app deployment) as PowerShell scripts, and deploy the
app as a Windows Service rather than under IIS. Keep whichever fits how you
actually want to run this - the GUI installer here matches the GraniteWMS
Install Wizard's pattern and hosts under IIS instead, which is closer to the
original "IIS-hosted web app" design intent. Both read the same `../sql/`
templates, so a fix to one process-step kit template applies to both paths.
