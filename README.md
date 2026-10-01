# Granite Toolkit

One Visual Studio solution for the GraniteWMS installer tools, with the code they share pulled into a single library instead of being copied into each one.

> **Status (v0.1.0, 2026-10-01): stage 1 of 3.** The four wizards are in one solution and run on a shared core. Each is still its own exe and behaves as before. No launcher yet.
> The solution builds with **0 warnings, 0 errors** (10 projects). Results from the logic harnesses match the separate repos exactly: Install Wizard **165 / 0 failed** (V6.0 release), BI Deploy **218 / 0 failed** (real BI script set), DB Switcher **275 / 0 failed**. The new shared-core harness: **21 / 0 failed**.
> None of the four has been run on Windows from this solution yet. See "Confirm on the next run".

## What's in it

| Project | What it is | Came from |
|---|---|---|
| `src/Granite.Toolkit.Core` | Shared code with no UI | new |
| `src/Granite.Toolkit.UI` | Shared Windows Forms pieces | new |
| `src/GraniteInstallWizard` | Core stack installer, v0.5.1 | `Granite Install Wizard` |
| `src/GraniteBiDeployWizard` | BI deployment wizard, v1.6.0 | `Granite BI Deploy\BI Deployment Wizard` |
| `src/GraniteDbSwitcher` | Local database switcher, v0.1.0 | `Granite DB Switcher` |
| `src/GraniteAttachInstaller` | Granite Attach installer, v0.1.0 | `Granite Attach\installer` |
| `tests/Harness.*` | LogicHarness checks per module, plus `Harness.Core` | each repo's `LogicHarness` |

The original folders were not touched. Their manuals, release notes, SQL scripts and (for Attach) the web app itself stay where they are.

## What moved into the shared core

| Shared piece | Was in | Notes |
|---|---|---|
| `Processes/ProcessRunner` | Install, DB Switcher, Attach | The copies were the same apart from namespace and comments |
| `Sql/SqlInstanceDiscovery` | Install, BI, Attach | Same |
| `Logging/LogEntry` + `LogLevel` | all four | Merged: the four had different `LogLevel` sets (BI had 4 members, Install 7). Now one set: Info, Success, Warning, Error, Detail, DryRun, Stage. `Prefix` came from Attach |
| `Iis/IisCommands` | Install, DB Switcher, Attach | Merged. Install's commands, DB Switcher's app/vdir parsers and tolerant XML reading, and Attach's `AppCmdExists`. One `IisSite` record (Name, Id, Bindings, State) replaces three. `AddSite` takes `protocol` (https by default, Attach passes http) |
| `Net/LocalAddressDiscovery` | Install, Attach | Install's version (Attach's was a cut-down copy) |
| `UI/WizardStepControl<TContext>` | Install, BI, Attach | Each module keeps a one-line `WizardStepControl` that sets its context type and hint width, so none of the step classes changed |

Each module has a `GlobalUsings.cs` that brings the shared namespaces in, so most module files didn't need editing at all.

### Deliberately left per module

These look similar across modules but are tied to each module's own context, so they stayed where they were for now:

- `SqlConnectionFactory` (Install and BI): different credentials, application names and rules about Windows auth.
- `IisService` (Install and DB Switcher): Install creates sites, DB Switcher finds and recycles them.
- Attach's firewall rule name stays `Granite Attach (port)`, so rules on existing installs are still found.
- Attach keeps treating an unreadable appcmd listing as empty. The shared parser throws on malformed XML, because the Install Wizard relies on that.

### Changes in behaviour

- **Attach installer now targets .NET 8** (it was .NET 10), the same as the other three. It built with no changes, and the published exe is self-contained, so the server needs nothing extra either way.
- BI Deploy's log levels now include Detail, DryRun and Stage. It doesn't use them yet, and its log console handles any level it doesn't know as Info.

## Building and publishing

On Windows with the .NET 8 SDK or later (10.0.401 builds .NET 8 targets fine):

- `Build-And-Test.cmd` builds everything and runs the harnesses that need no extra data.
- `Publish.cmd install | bi | switcher | attach | all` writes `dist\<Module>-v<version>.exe`. These are the same single-file, self-contained exes each repo's own `Publish.cmd` produced.
- Or open `GraniteToolkit.sln` in Visual Studio 2022.

Versions stay per module, in each module's csproj.

## Testing

| Harness | Run with | Needs |
|---|---|---|
| `Harness.Core` | `dotnet run --project tests\Harness.Core -c Release` | nothing |
| `Harness.DbSwitcher` | `dotnet run --project tests\Harness.DbSwitcher -c Release` | nothing (optionally a folder of real appsettings files) |
| `Harness.InstallWizard` | `dotnet run --project tests\Harness.InstallWizard -c Release -- "C:\Users\izakm\Documents\Granite WMS\Granite V6.0"` | a release folder or zip |
| `Harness.BiDeploy` | `dotnet run --project tests\Harness.BiDeploy -c Release` | the BI scripts at `/tmp/harness-scripts/` (the path is hard-coded in its Program.cs, as it was before) |

## Confirm on the next run

- Each of the four exes starts and its first step looks the same as before (a check that the shared `WizardStepControl` and hint widths are right).
- Install Wizard: a dry run on a machine that already has the first install. That exercises the shared site and app pool parsers against real appcmd output.
- DB Switcher: discovery still finds both installs and their pool states.
- Attach: install onto a test site, then check the firewall rule is named `Granite Attach (port)` and the site is http.

## Plan for the next stages

1. **Done:** one solution, shared core, four separate exes.
2. Move more shared code across: a common SQL connection and script runner (Install's `GraniteSqlScriptParser` and BI's `ScriptBatchParser` do overlapping jobs), shared log console and log file writing, and a shared main form shell.
3. A single launcher, `GraniteToolkit.exe`, that detects what's on the server and offers the modules. DB Switcher goes behind a developer option. NiFiDeploy joins as a module once that project has code.
