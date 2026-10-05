# GraniteWMS BI Deployment Wizard

A .NET 8 Windows Forms wizard that deploys the GraniteWMS BI reporting
database and schedules its background sync, without anyone hand-running
sqlcmd or configuring Task Scheduler by hand.

## Building

This needs to be built on Windows (WinForms only runs there), with the
**.NET 8 SDK**:

```
cd GraniteBiDeployWizard
dotnet restore
dotnet build -c Release
```

or just open `GraniteBiDeployWizard.csproj` in Visual Studio 2022 (17.8+)
and press F5. The app requests elevation on launch (see `app.manifest`) —
registering a scheduled task under another Windows account, and writing the
`.bat` wrapper next to the deployment scripts, both normally need it.

> **Status: builds clean on .NET 8 (`dotnet build -c Release`), confirmed on
> a real Windows machine.** This couldn't be compiled in the sandbox this
> project was written in — it has no route to nuget.org or any mirror (only
> npm/PyPI/crates/Go proxies and GitHub are reachable), so `dotnet restore`
> couldn't fetch `Microsoft.Data.SqlClient`, `TaskScheduler`, or the
> `Microsoft.WindowsDesktop.App.Ref` reference assemblies WinForms itself
> needs. The build in that sandbox surfaced three real compile issues that
> only a compiler catches (not review or the dependency-free harness below):
> `CheckedListBox` has no `ThreeState` property (confused with
> `CheckBox.ThreeState`); `NaturalFileNameComparer` needed to implement
> `IComparer<string?>`, not `IComparer<string>`, to match the nullable
> `Path.GetFileName` signature; and declaring DPI awareness in both
> `app.manifest` and the WinForms-generated `ApplicationConfiguration.Initialize()`
> triggers a WFAC010 warning, fixed by moving it to the
> `ApplicationHighDpiMode` project property instead. All three are fixed and
> the build is clean with zero warnings.
>
> **Confirmed with a full clean run against `Ultra\SQLEXPRESS`.** Every
> stage clicked through end to end on a real Windows machine: the dedicated
> login bootstrap, the Panel 2 reporting-views check, all six numbered
> deployment scripts (186 batches, 0 failed, 1 file correctly skipped),
> the initial `bi.usp_RunSync` populating the BI tables, granting the
> scheduled-task account rights, generating `Run_BI_Sync.bat`, and
> registering the Windows Scheduled Task — all without a single collation
> or "Invalid object name" error, confirming the source-views prerequisite
> check and the `COLLATE DATABASE_DEFAULT` fixes in `03_Sync_Engine.sql`
> hold up on a real repeat deployment, not just in isolation. I can't drive
> a Windows GUI myself, so this run-through was done directly on your
> machine, not by me.
>
> Separately, everything the engine does that doesn't need
> `Microsoft.Data.SqlClient` or `TaskScheduler` — the GO-batch parser,
> script scanner/classifier, `.bat` generator, path normalization — was
> also compiled *and run* in this session against your real script files
> (see `LogicHarness/` below), which caught two further real bugs beyond
> the review pass.

## Packaging and distributing to implementation resources

This wizard isn't a product that lives permanently on a client's server —
its whole job is done once a deployment finishes; the ongoing sync afterward
runs from `Run_BI_Sync.bat` and the Windows Scheduled Task, neither of which
needs the wizard itself to still be present. That points at a portable
utility a consultant carries to each engagement, not an installed
application, so there's no MSI/installer here and none is planned. An
installer would also fight `app.manifest`'s `requireAdministrator`
elevation — both ClickOnce and MSIX have their own trust/sandboxing models
that don't combine cleanly with full admin elevation, for no real benefit
here.

**Build a self-contained single-file `.exe`** rather than distributing the
plain `dotnet build` output:

```
cd GraniteBiDeployWizard
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

This lands at
`bin\Release\net8.0-windows\win-x64\publish\GraniteBiDeployWizard.exe` — one
file with the full .NET 8 runtime bundled in. Copy that single file to the
client's server (or wherever the deployment is being run from) and launch
it with **Run as Administrator**; it has no dependency on what's already
installed there. This matters specifically because client SQL Server boxes
are unpredictable environments — some may not have any .NET 8 runtime on
them at all, and a framework-dependent build fails to launch in that case
with an error most consultants have no way to diagnose on-site. The plain
`dotnet build -c Release` output described under "Building" above is still
the right thing to use for development and the fast local build/test loop;
it's only the copy that goes out the door to an implementation resource
that should be this self-contained publish instead.

**Versioning.** The `<Version>` property in `GraniteBiDeployWizard.csproj`
(currently `1.0.0`) is what stamps the wizard's own title bar at runtime
(`MainForm.VersionLabel`, read from the built assembly's `AssemblyVersion`
rather than hardcoded, so the title bar can't drift out of sync with what
actually got published) — bump it before each release that goes out, and
rename the published `.exe` to include it (e.g.
`GraniteBiDeployWizard-v1.0.1.exe`) before dropping it in your shared
location. That gives anyone reporting a problem a single, visible answer to
"which build were you running" without needing to dig into file properties.

**Distribution.** For now, a shared location your implementation team
already uses (SharePoint, a network drive, an internal repo) with the
latest versioned `.exe` and a short note of what changed is enough — the
same "read the README for the reasoning behind each fix" habit this project
has followed all along. No in-app update check or auto-updater is built
in, deliberately: at the current scale of "a handful of consultants running
occasional deployments," that would be real engineering effort spent
solving a problem a shared folder and a version number already solve.
Revisit this if usage grows to the point where consultants regularly run
outdated builds without noticing — an in-app version check against a
shared version file is the natural first step up from here, well short of
a full auto-updater.

## What each panel does

1. **SQL Server Connection** — server/instance, SQL login, and a Test
   Connection button that opens a throwaway connection to `master`. Test
   Connection also checks whether the login has the `dbcreator` or
   `sysadmin` server role (needed for the `CREATE DATABASE` step later). If
   it doesn't, an inline option appears: "create a dedicated SQL login for
   this deployment" — see below.
2. **Live Database Reporting Views** — confirms the source database already
   has its `dbo.vw_BI_*` reporting views, using the login from Step 1, and
   if not, deploys them from a picked script. A required step — Next is
   blocked until this has actually run and either finds the views or
   deploys them — see below.
3. **Database Names** — source (`GraniteLive` by default) and target BI
   (`GraniteLive_BI`) database names. These are substituted for every
   `$(SourceDb)` / `$(BiDb)` in the scripts, same as the SQLCMD variables
   the scripts already use.
4. **Script Folder** — pick the folder holding the `.sql` files. The wizard
   scans it and shows a checklist of what it found (see "Script selection"
   below for how it decides what starts ticked).
5. **Background Sync Schedule** — sync interval (5/15/30 min) and the
   Windows account the scheduled task runs as.
6. **Deploy and Schedule** — a live log console, a Start button that runs
   the deployment end to end, and Cancel.

## Auto-detected server and account lists

Both Panel 1's server field and Panel 5's account field are editable
dropdowns, not plain text boxes — typing a value that isn't listed still
works exactly as before, but where possible the wizard offers what it can
already see:

- **Panel 1 server list** (`Core/SqlInstanceDiscovery.cs`) combines two
  sources: instances actually installed on this machine, read straight from
  the registry key SQL Server setup writes
  (`HKLM\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL`) —
  instant, and works even with SQL Browser stopped; and instances
  discoverable on the network via `Microsoft.Data.Sql.SqlDataSourceEnumerator`
  — the same broadcast-based discovery SSMS's server dropdown uses, which
  runs in the background and fills in a few seconds later. Both are
  best-effort: a firewalled or Browser-service-off instance is perfectly
  reachable by name but won't show up in either list.
- **Panel 5 account list** (`Core/WindowsAccountDiscovery.cs`) lists this
  machine's enabled local user accounts via
  `System.DirectoryServices.AccountManagement`. Deliberately local-only —
  querying a domain for its full user list is a much bigger, slower, and
  more sensitive operation than this wizard has any business doing, so a
  domain account (`DOMAIN\svc-account`) is still typed in by hand.

Either source failing outright (locked-down machine, no network, missing
permissions) just means an empty or partial list — every field these feed
remains a fully usable free-text box regardless.

## Panel 1: creating a dedicated login when the given credentials can't create databases

This wizard is usually run against a client-owned server, and the only SQL
login handed to whoever runs it often can't run `CREATE DATABASE` — that
needs the `dbcreator` (or `sysadmin`) server role, which application-scoped
logins don't normally have. Rather than requiring the installer to already
have `sa`'s password, Panel 1 has a fallback: **"I don't have a login with
database-creation rights — create one"**. Expanding it lets you authenticate
once with an admin identity you *do* have — most commonly your own Windows
login on the machine, since a freshly installed SQL Server Express instance
usually makes local admins `sysadmin` automatically (Windows Authentication
is also supported here as a genuinely separate, explicitly opt-in path,
distinct from the "never falls back to Windows auth" guarantee that still
holds for the actual deployment connection) — and have the wizard run
`CREATE LOGIN`/`ALTER SERVER ROLE ... ADD MEMBER [dbcreator]` for a new,
dedicated login (`Granite_BI_User` by default; a strong random password can
be generated in the UI). That admin identity is used once, live, and is
never stored. Once the new login is confirmed to work and to have
`dbcreator`, it silently replaces whatever was in the username/password
boxes and becomes the one and only credential used for the rest of the
wizard — Panel 6's log just notes that a dedicated login was used, it never
echoes the password.

Because SQL Server automatically makes the creator of a database its
`db_owner`, granting `dbcreator` alone is enough — no separate grant on
`GraniteLive_BI` is needed once `01_Create_BI_Database.sql` runs.

**A second, separate grant: read access on the source database.** A real
run against `Ultra\SQLEXPRESS` surfaced this the hard way: `dbcreator` got
`GraniteLive_BI` created fine, but every script reading from the live
source database via a three-part name (`GraniteLive.dbo.vw_BI_...`) then
failed with "Invalid object name" — 95 failures, all from the same root
cause. `dbcreator` says nothing about what a login can see *inside* a
database it didn't create, and a brand-new dedicated login starts with zero
access to the existing source database. The bootstrap panel now also asks
for the source database name (pre-filled from Panel 3's default) and runs
`CREATE USER` / `ALTER ROLE db_datareader ADD MEMBER` there, using the same
admin connection, right after the dbcreator grant. This half specifically
needs the admin account to be sysadmin (or already `db_owner` of that
database) — a securityadmin-only account can create the login fine but has
no rights inside an arbitrary existing database — so it's reported as its
own pass/fail outcome rather than folded into the login-creation result.

## Panel 2: the "Invalid object name" errors that read-access alone didn't fix

The read-access grant above closed one gap, but a real run against
`Ultra\SQLEXPRESS` still failed with the exact same 95 "Invalid object name
'GraniteLive.dbo.vw_BI_...'" errors afterwards, even once the wizard
confirmed "Granted read access on [GraniteLive]." That ruled out permissions
as the cause — read access was proven to be in place and the errors didn't
change — so the actual scripts got read line by line instead of guessed at
again. `02_Create_BI_Tables.sql` builds every `bi.*` table with `SELECT
TOP (0) * INTO bi.X FROM [$(SourceDb)].dbo.vw_BI_X`: it expects those views
to already exist in the *source* database. Nothing in this BI deployment
kit creates them there — `07_Superset_Compatibility_Views.sql` creates
views with the same names, but inside the *BI* database, and its own header
comment says they "mirror the live GraniteWMS.dbo.vw_BI_* views exactly ...
so the same datasets, charts and dashboards keep working when you repoint
the connection." That only makes sense if the source-side views are a
separate, pre-existing artifact. Querying `GraniteLive` directly
(`SELECT name FROM sys.views WHERE name LIKE 'vw_BI_%'`) confirmed it:
zero rows. The environment simply never had them deployed — a missing
prerequisite, not a wizard bug, but one this wizard can now close by itself
instead of sending the installer off to run something by hand in SSMS.

**This check is now its own required step, not an aside sharing space with
something else.** It started as a collapsible section on Panel 1 the user
could tick past without noticing, then became a hard `ValidateStep` gate on
that same panel — an improvement, but a required gate still had to compete
for attention with Panel 1's actual job, connecting to the server, and
someone focused on getting the connection right could still miss a second,
unrelated gate appearing lower on the same screen. Since a missing
source-views prerequisite doesn't surface on its own until deep into Panel
6, after `CREATE DATABASE` and every `bi.*` table build have already run —
95 "Invalid object name" errors, discovered only once the deployment is
most of the way through, which is exactly what cost three full deployment
attempts working this out on `Ultra\SQLEXPRESS` — it's now
`Step2ReportingViewsControl`, a dedicated Panel 2 with nothing else on it
and nothing else to look at. `ValidateStep` blocks **Next** until this
check has actually run against the exact server and source database
currently entered, and either finds the views or the user has deployed
them successfully this session — editing the source database name
afterward, here or later on Panel 3, is treated as unchecked again, since a
different target could have different state.

Those views turned out to live in a completely different deployment kit —
a base Superset reporting-views script (`Granite_Superset_Views.sql` in
this client's case), normally run once against the live database as part
of the underlying Superset rollout, entirely independent of this BI sync
kit's own numbered scripts. Panel 2, **"Live Database Reporting Views,"** is
built entirely around this: it counts `vw_BI_%` views in the source
database with a single `sys.views` query (escaped as `vw[_]BI[_]%` so a
literal underscore can't accidentally match anything else); runs
automatically on arrival at the panel, using the SQL login already entered
on Panel 1; and, once a script file is picked (there's no default path —
this lives in a different folder on every client, so it's a plain file
picker, not an auto-detected location), executes it against the source
database with the wizard's own GO-batch engine (`ScriptBatchParser` — the
same one `DeploymentRunner` uses), re-checking the count afterwards so the
status line reflects what's actually there rather than what was attempted.
Creating views needs more than the read-only grant the dedicated login gets
above, so there's a "Use Windows Authentication instead" checkbox for
exactly the same reason the dedicated-login bootstrap panel has one: on a
fresh SQL Server Express box, the Windows account already running the
wizard is often the easiest admin identity available. The script itself is
safe to run more than once — it's written entirely with `CREATE OR ALTER
VIEW` — so re-running it (say, after Cradle ships a new view) is a normal,
harmless action, not a one-time-only step.

## Deployment run: self-healing the BI database's collation

With the source views in place, the next real run against `Ultra\SQLEXPRESS`
got past those 95 errors and hit a new, smaller one: three failures in
`03_Sync_Engine.sql`, all `Msg 468 "Cannot resolve the collation conflict
between Latin1_General_CI_AS and SQL_Latin1_General_CP1_CI_AS"`.
`01_Create_BI_Database.sql` already accounts for this in principle — when it
creates the BI database, it reads the source database's collation and
applies it via `CREATE DATABASE ... COLLATE ...` — but that logic sits
inside an `IF DB_ID($(BiDb)) IS NULL` guard, so it only ever runs at the
instant the database is first created. `GraniteLive_BI` on this environment
had already been created by an earlier deployment attempt — several runs
back, before the source-view prerequisite above was in place — so it locked
in the server's default collation at that moment, and every later run found
the database already existed, skipped the guard, and left the mismatch in
place no matter how many times the wizard was re-run.

The kit ships a standalone fix for exactly this, `00_Fix_Collation.sql`
(which is why it starts unticked on Panel 4 rather than deleted — this is a
real, documented scenario, just not the default path), but it hardcodes
`GraniteWMS`/`GraniteWMS_BI` rather than reading the names actually
configured, has to be run by hand outside the wizard, and its own header
comment says to then manually re-run `02`, `03`, `04` and `07` in order.
`DeploymentRunner` now runs the same three statements itself — `ALTER
DATABASE ... SET SINGLE_USER WITH ROLLBACK IMMEDIATE`, `ALTER DATABASE ...
COLLATE <source's collation>`, `ALTER DATABASE ... SET MULTI_USER` — using
the actual `$(SourceDb)`/`$(BiDb)` names, once per run, before any numbered
script executes (see `CollationRepairService`). Because that already runs
immediately before `02` through `08` execute in the same pass, the "then
re-run 02/03/04/07" step the manual fix calls for just happens automatically
as part of the very run that repaired the collation — nothing extra for the
user to remember or come back for. On a fresh install this is a silent
no-op: the database doesn't exist yet, so there's nothing to compare, and
`01` sets the right collation from the start exactly as before.

## Deployment run: the database-level collation fix wasn't enough on its own

Rebuilding `GraniteLive_BI` fresh with `CollationRepairService` in place (so
`01_Create_BI_Database.sql`'s own `CREATE DATABASE ... COLLATE ...` guard ran
as intended) did **not** clear the three `Msg 468` failures — the exact same
three, on the exact same lines, even with both databases now reporting the
same collation via `DATABASEPROPERTYEX`. That ruled out a database-level
mismatch as the cause and pointed at something narrower: the specific
columns named in `00_Fix_Collation.sql`'s own comment — `Basis`,
`MeasureType`, `Bucket` — are not plain copied columns, they're
literal/`VALUES`-derived in `Granite_Superset_Views.sql` (e.g. `'Received'
AS Basis`, `m.MeasureType AS MeasureType` from a table-value constructor).
A literal's collation is bound to the database the *view* is compiled in
(the source), while the corresponding column in the already-existing `bi.*`
table can end up bound differently depending on exactly how and when it was
first created — independent of whatever the two databases' own default
collations say today. Matching the database-level defaults doesn't reach
into that: it only affects new objects created after the `ALTER`, not
collation resolution for a literal already embedded in a view expression.

The fix that actually works for this, and the standard one for a Msg 468 of
this shape, is `COLLATE DATABASE_DEFAULT` on the specific comparison —
it forces one side of the comparison to an explicit, named collation, which
always wins over an implicit one, so it resolves correctly no matter which
side actually carries which collation. This was applied directly to
`03_Sync_Engine.sql` (not something `DeploymentRunner` can fix generically,
since it's the vendor script's own logic) — the three affected procedures'
`MERGE` statements:

- `bi.usp_Sync_StockAgeing` — `tgt.[Basis] = src.[Basis]` in the `ON`
  clause, and `src.[Bucket]` in the `EXCEPT` change-detection list.
- `bi.usp_Sync_StockMeasures` — `tgt.[Basis] = src.[Basis]` and
  `tgt.[MeasureType] = src.[MeasureType]` in the `ON` clause, and
  `src.[Bucket]` in the `EXCEPT` list.
- `bi.usp_Sync_TransactionMeasures` — `tgt.[MeasureType] = src.[MeasureType]`
  in the `ON` clause.

Each now carries `COLLATE DATABASE_DEFAULT` on the named side. A `.bak` copy
of the original file was left alongside it in the script folder. This
doesn't need re-verifying against a rebuilt database — the fix operates at
comparison time inside the stored procedure, not on any table's stored
column collation, so it's correct regardless of what the existing `bi.*`
tables' columns actually carry.

`CollationRepairService`'s database-level fix is still worth keeping: it's
what makes a *fresh* `GraniteLive_BI` come up with the right collation from
the start (as seen in this same run — "does not exist yet" — before its
`CREATE DATABASE ... COLLATE ...` ran cleanly), and it protects against a
database-level mismatch on some other object this kit adds later. It just
turned out not to be sufficient, on its own, for these three
literal-derived columns.

## Deployment run: the one-sided COLLATE fix recurred on a later rebuild

A later run against `Ultra\SQLEXPRESS` — `GraniteLive_BI` dropped and
rebuilt completely fresh again, per the log's own "does not exist yet -- it
will be created with [GraniteLive]'s collation" line — hit the *exact* same
three `Msg 468` failures, on the exact same lines, in the exact same three
procedures. First checked whether Dropbox (this kit's folder syncs from
`.../Dropbox Files/Granite Rapid Deploy/SuperSet/Granite BI DB/`) had
overwritten the fix with an older copy: it hadn't -- a byte-for-byte diff
against the `.bak` left when the fix was first applied confirmed all five
`COLLATE DATABASE_DEFAULT` clauses were still exactly where they were left,
untouched.

So the fix was intact and still failed. The closing claim in the section
above -- that a one-sided `COLLATE DATABASE_DEFAULT` "resolves correctly no
matter which side actually carries which collation" -- turned out to be
incomplete. Forcing *one* side to `DATABASE_DEFAULT` only guarantees a match
if the *other*, untouched side happens to already carry that same
collation. That held on the run the fix was validated against, but nothing
guarantees it holds on every fresh rebuild: a literal/`VALUES`-derived
column's actual bound collation, baked in at the moment `02_Create_BI_Tables.sql`'s
`SELECT INTO` first creates it, depends on the exact execution context at
that instant -- not reliably the same thing `DATABASEPROPERTYEX` reports as
the database's own default collation when read back later. Two rebuilds of
the same database, both reporting identical `DATABASEPROPERTYEX` collations
for `GraniteLive` and `GraniteLive_BI`, can still land their literal-derived
columns on different actual collations underneath.

The corrected fix removes that assumption instead of relying on it: apply
`COLLATE DATABASE_DEFAULT` to **both** sides of every comparison involving
`Basis`, `Bucket`, or `MeasureType`, not just one. With both operands
explicitly coerced to the same named collation at comparison time, the
result no longer depends on which side (if either) actually matches the
database default -- it can't conflict, regardless of what either column
natively carries. Updated in `03_Sync_Engine.sql`:

- `bi.usp_Sync_StockAgeing` -- `tgt.[Basis] COLLATE DATABASE_DEFAULT =
  src.[Basis] COLLATE DATABASE_DEFAULT` in the `ON` clause; both
  `src.[Bucket]` and `tgt.[Bucket]` now carry `COLLATE DATABASE_DEFAULT` in
  the `EXCEPT` change-detection list.
- `bi.usp_Sync_StockMeasures` -- same pattern for `Basis` and `MeasureType`
  in the `ON` clause, and both `src.[Bucket]`/`tgt.[Bucket]` in the `EXCEPT`
  list.
- `bi.usp_Sync_TransactionMeasures` -- same pattern for `MeasureType` in the
  `ON` clause (this proc's `EXCEPT` list doesn't touch a literal-derived
  column, so nothing else needed changing there).

Checked the rest of the file for the same risk while in there: every other
`MERGE` in `03_Sync_Engine.sql` keys on a plain numeric ID
(`TransactionId`, `TrackingEntityId`, `DocumentDetail_id`, `Location_id`,
`StockTakeLine_id`) with no literal-derived column in its `ON` clause or
`EXCEPT` list, so only these three procedures carry this risk. A fresh
backup, `03_Sync_Engine.sql.bak-20260827-collation-recurrence`, was left
alongside the original pre-fix `.bak` before making this change.

## Deployment run: the symmetric fix "didn't work" -- it was never running it

Re-running deployment after the symmetric-`COLLATE` fix above produced the
exact same three failures, on the exact same lines, again. That should have
been impossible -- both sides of every affected comparison were now forced
to an identical, explicit collation, which cannot conflict with itself. The
fix being correct and the failure still happening at the same spot meant
one thing: the server wasn't running the file that had been fixed.

It wasn't. `find` across every connected folder for `03_Sync_Engine.sql`
turned up two copies with different content:

- `.../Dropbox Files/Granite Rapid Deploy/SuperSet/Granite BI DB/` -- the
  live copy, edited (both times) by every fix above.
- `.../Claude/Granite BI Deploy/Source Files/Granite BI DB/` -- a SQL-scripts
  subfolder inside "Source Files", the project's reference archive (the
  implementation manuals, the architecture diagram, the Superset asset
  exports -- unrelated background material, not something this incident
  touches). This particular subfolder is a snapshot dated 2026-08-26. A
  byte-for-byte diff showed it was identical to the *original pre-fix*
  `.bak` -- it predated even the first collation fix, and it's also where
  `LogicHarness`'s test fixture was copied from (see "Verified:
  `LogicHarness/`" below) -- but it was never the live, actively-edited
  copy, and had quietly gone stale.

Panel 4's "Deployment Script Folder" is a plain folder picker with no
default and no memory of which folder is "the real one" (`Step3ScriptFolderControl`
just persists whatever path was last browsed to) -- entirely correct
behavior, and not something to fix in code. What went wrong is that Panel 4
had been pointed at the stale snapshot instead of the live folder for these
runs, so every fix landed in a file the wizard never read. Unblocked
immediately by copying the corrected `03_Sync_Engine.sql` into the stale
folder too, but that's a one-time patch, not a structural fix.

**The actual fix is procedural, not code**: there should be no standing
"live" folder to point at in the first place, on this machine or a
client's. A personal sync folder that also holds work in progress, or a
copy sitting next to the wizard's own project files, can silently drift out
of date the moment a fix lands anywhere else -- exactly what happened here,
twice. The correct pattern for every engagement, including this one, is to
copy the *current* deployment kit's numbered `.sql` files onto the target
server fresh, then point Panel 4 at that copy -- never at a folder that
lives with the wizard, and never at an ambient sync folder used for
anything else. `Step3ScriptFolderControl` now says this directly, right
under the folder picker, so it isn't just a README rule to remember.

Only that `Granite BI DB` subfolder is stale -- the rest of `Source Files`
(the manuals, diagram, and Superset exports) is fine as-is and wasn't
touched. A note, `DO_NOT_DEPLOY_FROM_HERE.txt`, was left inside just that
subfolder explaining why it shouldn't be used as a live deployment target.

## Deployment run: a fourth copy, found from the log itself, not a guess

The procedural fix above (copy the current kit onto the server, point Panel
4 at that copy) is still correct, but the very next run failed with the
exact same three lines again. This time the fix wasn't found by re-checking
the two known folders -- both were already correct and neither was what
Panel 4 was reading. It was found by reading the deployment log's own
`Wrote ...\Run_BI_Sync.bat` line literally: it named a path this session had
never had access to, `Granite WMS\Granite Rapid Deploy\SuperSet\Granite BI
DB\` -- similar to, but not the same as, either the Dropbox-synced folder or
the `Source Files` snapshot, and not one of this session's connected
folders. Requested access to it directly, and its `03_Sync_Engine.sql`
hashed identical to the untouched original -- confirming it had never been
fixed at all, in either round, because nothing had ever been able to reach
it. Applied the same symmetric-`COLLATE` fix there (backed up first, as
always); it now hashes identical to the corrected Dropbox copy.

Four real folders have now turned up over the course of this: the live
Dropbox-synced kit, the `Source Files` reference-archive snapshot, this one,
and (implicitly) whatever the very first successful run several days back
was actually pointed at, since this exact file had no earlier fix in it at
all. Rather than keep discovering these by inference, the reliable way
forward is simple: check what path Step 4 actually shows before assuming
which folder a fix needs to land in.

## Checking the scheduled sync is actually healthy after deployment

Deployment finishing successfully only confirms the *first* sync ran.
Whether the Windows Scheduled Task keeps firing every interval after that
is a separate question, and one the wizard itself doesn't answer (it
registers the task and moves on -- it doesn't stay resident to watch it).
`ops/Check_Sync_Log_Health.sql` answers it from the BI database side:
recent batches from `bi.SyncLog` at a glance, an overdue check against the
configured interval, a per-object breakdown of the latest run (which
matters when the batch summary shows `CompletedWithErrors` and you need to
know which of the ~20 sync procedures actually failed), and any failures in
the last 24 hours so an intermittent one doesn't hide behind a
healthy-looking latest batch. It's read-only and safe to run anytime.

It can't see the Windows Scheduled Task itself, though -- `bi.SyncLog`
only gets a row once `bi.usp_RunSync` actually starts, so a *disabled* or
misconfigured task leaves no trace there at all. The script's closing
comment has the one-line PowerShell (`Get-ScheduledTaskInfo`) to check
that side: last run result, last run time, and next run time.

## Panel 5: giving the scheduled-task account its own SQL login

The recurring scheduled sync (`Run_BI_Sync.bat`) authenticates with `sqlcmd
-E` — Windows/trusted authentication as the Panel 5 execution account — not
a SQL login, and a freshly created Windows account almost never has one.
Panel 5 has the same kind of fallback as Panel 1: **"This account doesn't
have a SQL login yet — create one"**. It uses an admin identity (again,
Windows Auth as the current user by default, or a different SQL login) with
`sysadmin`/`securityadmin` to run `CREATE LOGIN [DOMAIN\svc-account] FROM
WINDOWS;` — a no-op if that login already exists, so it's safe to run again
on a redeploy.

This step only maps the *server-level* login; it doesn't grant it anything
inside `GraniteLive_BI` yet, because at the point the user is on Panel 5,
that database usually doesn't exist. Instead, `DeploymentRunner` does that
part itself at the end of Step 6, right after the deployment scripts finish
and the connection is confirmed to be sitting in the BI database: it
creates a database user for the account and runs `GRANT EXECUTE ON
bi.usp_RunSync`, using the same deployment login that either already had
enough rights or (via the Panel 1 bootstrap) automatically became
`db_owner` of the database it just created. If Panel 5's step was skipped
or the account still has no server-level login by then, this logs a clear,
non-fatal warning pointing back at it — the scheduled task still gets
registered, it just won't succeed until that's fixed.

**A ".\" local account needs its computer name, not the dot, once SQL
Server is involved.** A real run showed Verify Account (LogonUser) pass
clean for `.\izakm`, then Create/Verify SQL Login fail on that exact same
string with `Windows NT user or group '.\izakm' not found`. LogonUser and
Task Scheduler both treat "." as shorthand for "this computer" -- SQL
Server's `CREATE LOGIN ... FROM WINDOWS` does not, and needs the literal
computer name instead. `BootstrapLoginService.ResolveForSqlServer` expands
`.\name` to `COMPUTERNAME\name` before every SQL-visible use of a Windows
account name (creating the login, and later looking it up to grant
`bi.usp_RunSync` rights), so the name actually created and the name later
searched for always match. This only makes sense when the wizard runs on
the same machine as the SQL Server instance -- true for the common case
this feature targets, but worth knowing if the two are ever different
machines, where a ".\name" account wouldn't identify the right one anyway.

**The same ".\" problem also hit Task Scheduler itself.** A run past both
earlier fixes still failed at the very last step, registering the
scheduled task, with a cryptic `(21,8):UserId:` exception -- Task
Scheduler's task-definition XML validates the `<Principal><UserId>`
element against a schema that, like SQL Server, does not accept the ".\"
shorthand LogonUser resolves natively. `ScheduledTaskService` now resolves
`context.WindowsAccountName` through the same
`WindowsAccountNameResolver.ResolveLocalShorthand` used by the SQL-side fix
above (extracted into its own small class once it was needed in two
places) immediately before calling `RegisterTaskDefinition`, so a ".\name"
account works there too instead of failing with a message that gives no
hint what's actually wrong.


**Credentials are verified up front, not discovered at the end.** The same
real run also showed why this matters: a bare account name ("Izakm", no
domain/computer qualifier) was typed in, the wizard ran the *entire* SQL
deployment (dozens of batches, several seconds), and only at the very last
step did Task Scheduler reject it with "The user name or password is
incorrect. (0x8007052E)" — meaning a fix meant sitting through the whole
deployment again to find out if it worked. Panel 5 now: (1) normalizes a
bare name like `svc-bi` to `.\svc-bi` (explicitly local) before it's used
anywhere, so Task Scheduler and `CREATE LOGIN ... FROM WINDOWS` both see an
unambiguous account instead of guessing; and (2) verifies the account and
password against Windows itself — via the same `LogonUser` Win32 API
Windows uses internally, `Core/WindowsCredentialValidator.cs`, no profile
loaded, token closed immediately — both on demand via a "Verify Account"
button and as a hard gate in `ValidateStep`, so leaving Panel 5 with a bad
password is no longer possible. It's a short, blocking call (LogonUser is
normally sub-second for a local account), which is a deliberate trade-off:
a moment's pause on Next beats discovering the problem after the whole
deployment has already run.

## Panel 5: choosing the scheduling mechanism (v1.1.0)

Until this version, Panel 5 always registered a Windows Scheduled Task,
unconditionally -- there was no real choice, even though the deployment kit
also ships `06_Scheduler_SqlAgent.sql` for SQL Server Agent (Standard/
Enterprise only; Express has no Agent service). Ticking that file on Panel 4
ran it *in addition to* the Windows Task getting registered, which is
redundant (harmless, since `bi.usp_RunSync`'s app-lock keeps them from
colliding -- a blocked run just logs `Status = 'Skipped'` -- but confusing,
and not an actual choice).

Panel 5 now has an explicit radio-button choice: **Windows Task Scheduler**
(default, works on Express and Standard/Enterprise) or **SQL Server Agent**
(Standard/Enterprise only). Panel 6 registers exactly the one chosen, never
both, regardless of what's ticked on Panel 4.

**Edition detection.** As soon as Panel 5 is shown, it runs
`SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)` against the Panel 1
server and disables the SQL Server Agent radio (with an explanation) if the
result is `4` (Express). A failed check -- a transient connection issue, not
"this is Express" -- leaves the option enabled with a note, deliberately:
wrongly disabling a valid option on a check that merely didn't complete
would be worse than the alternative of finding out at Panel 6 that Agent
genuinely isn't there (a `sp_add_job` failure at worst, no data is at risk).

**SQL Server Agent implementation: a second, independent one, not the kit
script itself.** `06_Scheduler_SqlAgent.sql` hardcodes a 15-minute interval
(`@freq_subday_interval = 15`), which only matches Panel 5's choice by
coincidence. Rather than teach the wizard's `$(SourceDb)`/`$(BiDb)`
substitution a third token and depend on the file being ticked on, Panel 6
now calls a new `Core/SqlAgentSchedulerService.cs` that runs the same
`sp_add_job`/`sp_add_jobstep`/`sp_add_schedule`/`sp_attach_schedule`/
`sp_add_jobserver` sequence directly, parameterized with whatever interval
was actually chosen. The kit script's header now says as much, so a DBA
reading it for a manual, non-wizard deployment isn't misled into thinking
it's wired to Panel 5. Two independent implementations of the same job
shape is a real trade-off (change the shape, remember to change it in both
places) but was preferred over reading-and-patching a file on disk for
something this security-sensitive (a msdb job) -- see the standing note in
this README about SQL scripts being read from disk at runtime, not
compiled in; this is the one case where that pattern didn't fit.

**No Windows account needed for the Agent path.** A SQL Server Agent TSQL
job step runs as its owner, which defaults to whoever creates it -- the
Panel 1 SQL login, which already has every right it used to deploy the rest
of the BI database. So Panel 5's Windows-account/password/bootstrap section
(all of it specific to the `-E` trusted-auth Task Scheduler path) is now
hidden entirely when SQL Server Agent is chosen, and `ValidateStep` skips
validating those fields on that path -- they were previously mandatory
regardless of mechanism, which would have blocked an Agent-only deployment
on fields that mechanism doesn't use.

**What this deliberately does not include yet: Granite Scheduler.** The
Superset-deployment planning pass (see below) turned up a real, documented
mechanism for this -- a `ScheduledJobs` table (`StoredProcedure`, `Interval`,
`IntervalFormat`, `isActive`) that Granite's own Scheduler service polls --
but two things aren't confirmed anywhere: the actual value format
`Interval`/`IntervalFormat` expect, and whether that service can call a
procedure in a different database (`ScheduledJobs` lives in the Granite
live database; `bi.usp_RunSync` lives in the separate BI database). Same
gap the vendor's own installation guide already flagged as needing
product-team confirmation. Rather than ship a third option that might
silently register a job that never fires, it's left out until confirmed.

## Panel 5: a third choice -- skip scheduling for now (v1.1.1)

Added a third radio button alongside Windows Task Scheduler and SQL Server
Agent: skip scheduling entirely. For a client who is not ready to decide
yet, or who wants to schedule the sync themselves (including, eventually,
through Granite Scheduler once that option is confirmed -- see the planning
note above). Choosing it hides the interval picker and the whole
Windows-account section, since neither means anything when nothing is being
registered, and `ValidateStep` skips all of that path's checks.

Panel 6 still generates Run_BI_Sync.bat on this path (it costs nothing and
gives whoever picks up scheduling later a ready-made wrapper to point at),
but registers no Windows Scheduled Task and no SQL Server Agent job, and
logs a clear warning that the BI tables will fall behind after the initial
sync until something runs it on a recurring basis.

## Panel 5: adding Granite Scheduler (v1.2.0)

The planning note under v1.1.0 above left Granite Scheduler out because two
things weren't confirmed: the actual `Interval`/`IntervalFormat` value
format, and whether the Scheduler service can call a procedure in a
different database than the one `dbo.ScheduledJobs` lives in. Both are now
confirmed from real evidence, so Panel 5 gets a third real option
alongside Windows Task Scheduler and SQL Server Agent (skip/manual stays a
fourth, from v1.1.1).

**What was confirmed, and how.** Three screenshots of a live install: the
blank Scheduled Jobs WebDesktop form, a grid of two real working jobs
(`HandleLabelPrintQueue`, `InventorySnapshot`) with populated columns, and
a direct SSMS query against `dbo.ScheduledJobs` / `ScheduledJobInput` /
`ScheduledJobsHistory`. Together they show `Type = 'STOREDPROCEDURE'` and
`InjectJob = NULL` for a stored-procedure job, and `IntervalFormat` taking
either `'SECONDS'` (with `Interval` as a plain integer, e.g. `15`) or
`'CRON'` (with `Interval` as a standard cron expression, e.g. `0 0 * * *`).
This wizard uses the `'SECONDS'` form, since a fixed-minutes interval maps
onto it directly. Every real `StoredProcedure` value seen is a bare,
unqualified name with no schema or database prefix -- confirming the
Scheduler executes it in whatever database `ScheduledJobs` itself lives in
(the live Granite database), not a separate one.

**The cross-database problem, and the proxy procedure.** `bi.usp_RunSync`
lives in the separate BI database, so a bare name in `ScheduledJobs` can't
reach it directly. `Core/GraniteSchedulerService.cs` creates a thin proxy
procedure in the live database first -- `dbo.usp_RunGraniteBiSync`, which
just does `EXEC [BiDb].bi.usp_RunSync;` -- and registers that bare name in
`ScheduledJobs` instead. Creating an object in the live database this way
isn't a new category of risk for this wizard: Panel 2 already does the
same thing for the `dbo.vw_BI_*` reporting views.

**The Scheduler service's SQL login, and the EXECUTE grant.** The
remaining open question from v1.1.0 -- which SQL login the Scheduler
service itself connects as, needed to grant it rights into the BI
database -- is now confirmed from a real `appsettings.json` on a live
install: its `ConnectionStrings:GraniteConnection` uses SQL login
`Granite`. (That file's connection string also contained the login's
password in plain text -- it was never read, logged, or written anywhere
by this wizard or by Claude; only the login *name* informed the design.)
`GraniteSchedulerService` now grants EXECUTE on `bi.usp_RunSync` to that
specific login -- creating a database user for it in the BI database
first if one doesn't already exist -- and falls back to the broader
`public` role only if no `Granite` login is present on the target
instance, since a different install may run the service under a
different account and a job that silently can't reach its own procedure
is worse than a slightly broader grant.

**Least field-tested of the three mechanisms.** Windows Task Scheduler and
SQL Server Agent have both been run for real through this wizard; Granite
Scheduler hasn't yet. Everything above is grounded in real screenshots and
a real config file, not guesswork, but please confirm on the first live
deployment that the job actually fires -- check `dbo.ScheduledJobsHistory`
for a `SUCCESS` row after the chosen interval elapses -- before relying on
it for a client.

## Deployment run: two real bugs from the first Granite Scheduler run (v1.2.1)

The very first real run through v1.2.0's new Granite Scheduler option (see
above) turned up two genuine bugs, both from a live console log:

```
[11:09:40] Collation check: Could not check/repair the BI database's collation:
The column 'SyncLog.DurationMs' is dependent on database collation. The
database collation cannot be changed if a schema-bound object depends on it.
...
[11:12:54] Registering Granite Scheduler job 'GraniteBiSync' (every 15 min)...
[11:12:54] Deployment failed: CREATE PROCEDURE permission denied in database 'GraniteLive'.
```

**Bug 1: `bi.SyncLog.DurationMs` blocks the collation repair.** `DurationMs`
is a computed column (`DATEDIFF(millisecond, StartTime, EndTime)`) -- purely
numeric, no string expression anywhere in it -- but SQL Server refuses
`ALTER DATABASE ... COLLATE` while *any* computed column exists in the
database, regardless of its type, because the column's definition is parsed
and bound under the database's current collation. `CollationRepairService`
now checks for `bi.SyncLog.DurationMs` and its index
(`IX_bi_SyncLog_BatchTime`) before running the three COLLATE statements,
drops them if present, and recreates both afterward using the same
definitions `01_Create_BI_Database.sql` creates them with -- guarded by an
existence check either way, so it's a no-op on a database that doesn't have
`bi.SyncLog` yet.

This run's collation check failing didn't actually block the deployment --
186 batches executed, 0 failed, and the initial sync completed -- because
this particular `GraniteLive_BI` already happened to be on a workable
collation from an earlier run. Left unfixed, though, it would have kept
failing silently (logged only as a `Warning`) on any environment where the
mismatch is real, defeating the entire point of `CollationRepairService`.

**Bug 2: a scheduler-registration failure was reported as a whole-deployment
failure.** The same run's actual new bug: the deployment login could create
the source-database reporting views on Panel 2 (`CREATE VIEW`) but not the
Granite Scheduler proxy procedure on Panel 5/6 (`CREATE PROCEDURE`) -- two
separate grantable SQL Server permissions, and this install's login only had
the first. That's a legitimate, expected kind of permission gap (the same
class Panel 1's dedicated-login bootstrap and Panel 2's read-access fix
already exist to handle) -- but the exception from
`GraniteSchedulerService.RegisterOrUpdateSyncJobAsync` was uncaught inside
`Step5DeployLogControl`, so it bubbled out to the generic top-level handler
and reported "Deployment failed", even though the real work -- schema,
views, indexing, and a 3-minute initial sync -- had already finished
successfully moments before.

Scheduler registration is now wrapped in its own `try`/`catch`, separate
from the data-deployment result above it. A failure there logs a clear,
mechanism-specific message instead (for the `CREATE PROCEDURE` case
specifically: the exact `GRANT`/`db_ddladmin` fix, and that Windows Task
Scheduler or SQL Server Agent are available as alternatives that don't need
this permission), and the status line now distinguishes "deployment
complete, but scheduling could not be registered" from either a full
success or a real data-deployment failure -- so re-running Start Deployment
after fixing the permission is understood as *refreshing* already-deployed
data, not redoing failed work from scratch.

## Deployment run: v1.2.1's own fix left the BI database stuck single-user (v1.2.2)

The v1.2.1 fix above shipped, and the very next real run hit a new failure
caused by it:

```
[11:29:30] Collation check: Could not check/repair the BI database's collation:
Database 'GraniteLive_BI' is already open and can only have one user at a time.
[11:29:30] --- 01_Create_BI_Database.sql ---
[11:29:40] 01_Create_BI_Database.sql line 24: Changes to the state or options
of database 'GraniteLive_BI' cannot be made at this time. The database is in
single-user mode, and a user is currently connected to it.
[11:29:42] Aborting deployment: without a working [GraniteLive_BI] ...
```

Two bugs, cause and effect. The real root cause is older than v1.2.1: the
original `SINGLE_USER` -> `COLLATE` -> `MULTI_USER` sequence had no
`try`/`finally` around it, so when the *previous* run's `COLLATE` failed on
the `SyncLog.DurationMs` dependency (the very bug v1.2.1 fixed), it failed
**after** `SINGLE_USER` had already been claimed and **before**
`MULTI_USER` ever ran -- leaving `GraniteLive_BI` permanently locked to
whichever session held it once that run's own connection closed (a stray
SSMS tab, in this case). v1.2.1 then made the symptom worse: its new
existence check for `bi.SyncLog.DurationMs` ran *before* re-claiming
`SINGLE_USER`, so on a database already stuck single-user from someone
else, that check itself failed immediately with "already open," and the
run aborted even earlier than before -- at the collation check, rather than
three `Msg 468`s into `03_Sync_Engine.sql`.

Two changes in `CollationRepairService` fix this for good, not just patch
around this one instance: `ALTER DATABASE ... SET SINGLE_USER WITH ROLLBACK
IMMEDIATE` now runs *first*, unconditionally, before the SyncLog check or
anything else -- `WITH ROLLBACK IMMEDIATE` forcibly reclaims the database
even if another session currently holds it, so this run self-heals the
stuck state left by the last one rather than being blocked by it. And
everything from the SyncLog check through `COLLATE` to the SyncLog rebuild
is now wrapped in `try`/`finally`, with `SET MULTI_USER` in the `finally` --
best-effort, but unconditional -- so no future failure in that sequence,
whatever it turns out to be, can strand the database single-user again.

No manual SSMS intervention needed to unstick the current live server: the
next deployment run's own `SET SINGLE_USER WITH ROLLBACK IMMEDIATE` reclaims
it as the first thing it does.

## Panel 6: granting the deployment login full rights, permanently (v1.3.1)

Three real deployment attempts in a row (see the collation and Granite
Scheduler sections above) each turned up a different missing grant on the
Step 1 login: db_datareader on the source database first, then CREATE
PROCEDURE there for the Granite Scheduler proxy procedure. Discovering
these one at a time -- run, fail, get told what's missing, fix it by hand,
run again -- works, but it's slow, and there was no reason to expect that
run's permission gap would be the last one.

Panel 6 now has a permanent, always-visible section -- **"Deployment login
rights (granted automatically, every run)"**. It follows the same shape as
Panel 1's and Panel 5's existing bootstrap flows -- an admin identity
(Windows Authentication as the current user by default, or a different SQL
login) entered once per run, live, never stored, used purely to run a
couple of privileged statements. Every Start Deployment click now begins
with it: `Core/DeploymentRightsService.cs` grants the Step 1 login
`dbcreator` at the server level (so it can create the BI database if it
doesn't exist yet) and `db_owner` on the source database, before
`DeploymentRunner` runs anything else.

**An earlier version of this (v1.3.0, same day) granted these temporarily
and revoked them again once each run finished.** That's the more
conventional least-privilege shape, but it missed how this wizard is
actually used: the same login redeploys to the same server again and
again, so revoking the rights right after granting them just meant hitting
the identical permission wall on the very next run. Feedback on that
version was direct: the login should simply have the rights it needs,
standing, like any other application login would -- so the revoke step is
gone. Granting something a login already has is a harmless no-op
(`ALTER SERVER ROLE ... ADD MEMBER`, `ALTER ROLE db_owner ADD MEMBER`, the
same idempotent shape used everywhere else in this codebase), so running
this on every single deployment costs nothing once the rights are already
in place -- the admin identity is still asked for every time, but after
the first successful run it isn't really doing anything new.

**A SQL Server Agent job step still gets its own explicit grant, not just
inherited db_owner rights.** A job step runs as its owner -- the login that
created it -- for as long as the job exists, not just during the run that
registered it. That's less fragile now that the login's rights are
permanent, but `SqlAgentSchedulerService` grants it `EXECUTE` on
`bi.usp_RunSync` explicitly anyway, as its own last step, so the job's
correctness doesn't depend on this wizard's rights-management strategy at
all. Windows Task Scheduler and Granite Scheduler don't have this
dependency to begin with: the first authenticates as a separate Windows
account via trusted auth, and the second already grants the Scheduler
service's own "Granite" login what it needs.

## Deployment run: Granite Scheduler registration failed on a truncated column (v1.3.2)

With v1.3.1's rights grant in place, a full real run finally got all the way
through data deployment (186 batches, 0 failed) and the initial sync
(`EXEC bi.usp_RunSync`), and only then hit a brand new error registering the
Granite Scheduler job:

```
Could not register the Granite Scheduler job: String or binary data would be
truncated in table 'GraniteLive.dbo.ScheduledJobs', column 'JobDescription'.
Truncated value: 'Refreshes the GraniteLive_BI reporting tables from the
vw_BI_ views. Created by the GraniteWMS BI De'.
```

**What the error itself proves.** Modern SQL Server's truncation error
includes the value as it was actually cut off, up to the column's real
limit. Counted in groups of ten characters, that truncated value is exactly
100 characters long -- so `dbo.ScheduledJobs.JobDescription` is confirmed to
be `nvarchar(100)` (or equivalent). This is a genuinely new schema fact:
nothing in the earlier screenshots showed it, since the two real jobs seen
there (`HandleLabelPrintQueue`, `InventorySnapshot`) both had short
descriptions that never came close to the limit.

**Why it wasn't hit sooner.** The description text built in
`GraniteSchedulerService.cs` -- `"Refreshes the {BiDb} reporting tables from
the vw_BI_ views. Created by the GraniteWMS BI Deployment Wizard."` -- runs
to about 116 characters once `BiDb` is filled in with a real name like
`GraniteLive_BI`, roughly 16 over the column's limit. Every earlier run
failed before reaching this statement (collation, then permissions), so this
is the first run to ever get far enough to exercise it.

**The fix.** `GraniteSchedulerService.cs` now clamps the built description to
100 characters right before it's used, rather than trusting the wording to
always fit:

```csharp
string jobDescription =
    $"Refreshes the {context.BiDb} reporting tables from the vw_BI_ views. " +
    "Created by the GraniteWMS BI Deployment Wizard.";
if (jobDescription.Length > 100)
{
    jobDescription = jobDescription.Substring(0, 100);
}
```

This is deliberately defensive rather than just shortening today's wording:
a future, longer `BiDb` name (client databases aren't always named
`GraniteLive_BI`) could push the sentence back over the limit even if the
current wording happens to fit, and a silently-truncated SQL parameter is a
much better failure mode than another aborted deployment.

**Where this leaves Granite Scheduler.** Every blocker hit against a real
install so far -- the collation dependency, the single-user stranding
regression, the missing `CREATE PROCEDURE` right, and now this truncation --
is resolved. This is the closest run yet to a fully clean Granite Scheduler
registration; the next real run is expected to get all the way through.

**A follow-up run did register cleanly -- and surfaced a red herring, not a
new bug.** `dbo.ScheduledJobsHistory` showed no new rows afterwards, for any
job, including the pre-existing `HandleLabelPrintQueue`. That looked at
first like the real Granite Scheduler service had crashed. It hadn't -- the
test SQLEXPRESS instance this wizard was being run against is a local
database with none of the Granite web tier (IIS, the Scheduler service
included) installed alongside it, restored from a copy of a client's
`GraniteLive` database. `sys.databases.create_date` confirms exactly when:
**2026-07-27 15:31:52**. The last real history row -- the final
`HandleLabelPrintQueue` tick before everything goes quiet -- is timestamped
**2026-07-27 15:11:45**, about twenty minutes earlier, on the same day. That
gap is the restore itself: the backup captured the client's live data as of
~15:11, and the local database finished being created/restored from it
~20 minutes later. Nothing stopped "yesterday" -- this is a month-old
snapshot (relative to 2026-08-28), and its last recorded activity simply
marks the moment it was captured, not a live incident. Once that data
landed on a machine with nothing polling `ScheduledJobs`, no further rows
were ever going to append, regardless of anything this wizard does. The
registration itself (the row landing correctly in `dbo.ScheduledJobs`) is
everything within this wizard's responsibility, and that part is confirmed
working. Confirming that a registered job actually *fires* needs an
environment where the real Granite Scheduler service is running --
realistically, the client's own server -- not a local database copy with no
application tier behind it.

## Deployment run: a client server rejected the login from the BI database it had just created (v1.4.0)

The first real run against an actual client server (`USA-GRANITEHCO-\SQLEXPRESS`,
not Izak's own test machine) got past the rights grant, connected, and ran
`01_Create_BI_Database.sql` -- which creates `[GraniteLive_BI]` -- cleanly.
The very next step failed:

```
[15:57:47] 01_Create_BI_Database.sql line 36: could not switch to
[GraniteLive_BI]: The server principal "Granite_BI_User" is not able to
access the database "GraniteLive_BI" under the current security context.
```

**Why this is surprising.** `CREATE DATABASE` normally makes the creating
login the new database's owner automatically -- no separate grant needed,
and this exact script had worked end to end, repeatedly, on Izak's own
SQLEXPRESS instance. The same login, in the same connection, that had just
successfully created `[GraniteLive_BI]` moments earlier could not then `USE`
it. The most likely explanation: this client's SQL Server instance has a
security-hardening policy (commonly a server-level DDL trigger) that
reassigns a freshly created database's ownership away from whoever created
it -- a real, if less common, enterprise configuration that Izak's own dev
instance simply doesn't have. Whatever the exact mechanism, the effect is
the same: creation no longer implies access on every server.

**The fix: don't rely on implicit ownership -- grant explicitly, once, and
retry.** `DeploymentRightsService` gained `EnsureDatabaseAccessAsync`, which
does the same idempotent `CREATE USER` / `ALTER ROLE db_owner ADD MEMBER`
shape already used for the source database, but against an arbitrary
database name using the same privileged admin identity Panel 6 already
collects. `DeploymentRunner` now calls it as a one-time repair exactly when
a `USE`/`ChangeDatabase` into `[BiDb]` specifically (never the source
database, which Panel 1 already validated access to) fails this way: log a
warning, grant access back explicitly, and retry the switch once before
falling back to the original abort-the-whole-run behaviour. If the repair
itself fails (e.g. the admin identity isn't privileged enough either), the
run aborts exactly as it always did, with the same actionable message.

This can't be reproduced locally -- Izak's own instance has never exhibited
this behaviour -- so it's confirmed against this one real client server and
should be watched on the next few deployments to different clients to see
how common the underlying policy actually is.

## Default window size increased ~25% (v1.4.1)

The default 720x620 window meant resizing it by hand on nearly every
launch to comfortably see the Step 5 log console and the Step 4 script
lists. Bumped to 900x780 (minimum 850x700, scaled the same ~25%) so it
opens usable-sized without that step. Purely cosmetic -- no behaviour
change.

## Deployment run: Granite Scheduler registration failed with "login already has an account under a different user name" (v1.4.2)

A real run against the same client server (`USA-GRANITEHCO-\SQLEXPRESS`),
this time deploying under a different SQL login named `Granite` (not
`Granite_BI_User`), got all the way through data deployment cleanly --
215 batches executed, 0 failed, initial sync completed, and the
v1.4.0 self-heal for the BI database switch didn't even need to fire
(the log showed a direct "Switched context to [GraniteLive_BI]."). It
then failed at the very last step, registering the Granite Scheduler
job, with:

```
Could not register the Granite Scheduler job: The login already has an
account under a different user name. Cannot find the user 'Granite',
because it does not exist or you do not have permission.
```

This is SQL Server error 15063. A single login can only be mapped to
one database user per database. On this server the login `Granite`
already owned `GraniteLive_BI` -- it had just created the database
(or been granted `db_owner`), which SQL Server maps automatically as
the `dbo` principal, not as a user literally named `Granite`. The
Scheduler registration's own grant step checked for an existing
principal by matching the name `Granite` directly:

```sql
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'Granite')
    CREATE USER [Granite] FOR LOGIN [Granite];
```

That check doesn't see the existing `dbo` mapping (it's a different
name), so it went ahead and tried `CREATE USER [Granite] FOR LOGIN
[Granite]` -- which SQL Server rejects, because the login `Granite`
already has a mapping (as `dbo`) and a login can't have two.

The fix, in `GraniteSchedulerService.cs`: resolve the login's *actual*
existing user name first, by joining `sys.database_principals` to
`sys.server_principals` on `sid` (not by matching the name string),
and grant to whatever name that resolves to:

```sql
DECLARE @existingUser sysname = (
    SELECT dp.name
    FROM sys.database_principals dp
    JOIN sys.server_principals sp ON dp.sid = sp.sid
    WHERE sp.name = N'Granite');

IF @existingUser IS NULL
BEGIN
    CREATE USER [Granite] FOR LOGIN [Granite];
    SET @existingUser = N'Granite';
END

EXEC(N'GRANT EXECUTE ON bi.usp_RunSync TO ' + QUOTENAME(@existingUser) + N';');
```

If the login is already `dbo`, this resolves `@existingUser` to
`dbo`, skips `CREATE USER` entirely, and the `GRANT` is a harmless
no-op (`dbo` already has full rights). If the login has no mapping at
all yet, it behaves exactly as before.

`SqlAgentSchedulerService.cs`'s equivalent grant method
(`GrantOwnerExecuteRightsAsync`, used for the SQL Agent scheduling
path) had the identical latent bug -- a name-based
`WHERE name = @name` check before `CREATE USER` -- even though it
hadn't been hit in a real log yet. Fixed the same way, preventively.

This was the last unresolved step in an otherwise clean run against
this client server: collation, read/write permissions, the
single-user stranding, the JobDescription truncation, and BI database
ownership have all now been hit and fixed against real evidence, and
this was the newest (and, so far, last) one.

## Skip the initial sync on a redeploy, when the BI database is already current (v1.5.0)

Every deployment run -- including a redeploy that's only adding a new view
or rerunning after a schema tweak -- ran the full `EXEC bi.usp_RunSync`
unconditionally after the scripts finished, even when the BI tables were
already fully populated and only minutes stale from the scheduled task.
On a large source database that full pass can take several minutes (the
last real client run took just under 3), which is wasted time when a
scheduler is already registered and would bring the tables current on its
own shortly anyway.

Now, right before running it, the wizard checks `bi.SyncLog` (the same
table `ops/Check_Sync_Log_Health.sql` reads) for the most recent completed
batch -- `Status` of `Success` or `CompletedWithErrors`, meaning data
actually landed; a `Skipped` row (another run was already in progress)
doesn't count as evidence the tables are populated. See
`SyncStatusService.GetLastCompletedSyncAsync`.

- **No completed batch found** (`bi.SyncLog` doesn't exist yet, or has no
  qualifying row) -- this is a genuinely first-ever sync. Runs
  automatically, no prompt, exactly as before.
- **A completed batch is found** -- the log shows how long ago it finished
  and its status, then a dialog asks whether to re-sync everything now or
  skip and let the scheduled task catch up. The message differs depending
  on whether a scheduler is registered for this deployment (Panel 5):
  with one, skipping is safe -- it'll be caught up within about one
  interval; with `SchedulerType.None` chosen, the dialog says plainly that
  skipping leaves the tables as they are until the sync is run by hand.
  Defaults to "re-sync now" (Button1) if the dialog is dismissed without a
  clear choice, so an inattentive click doesn't silently leave stale data.

The decision point is a `Func<DeploymentRunner.SyncSkipPromptContext, bool>`
passed into `DeploymentRunner`'s constructor (optional -- null means
"always sync", same as every prior version), kept separate from the
`Action<LogEntry> log` callback it already took. `Step5DeployLogControl`
wires it to `ConfirmResync`, a `MessageBox.Show` -- safe to call directly
from inside `RunAsync`'s flow because nothing in this codebase uses
`ConfigureAwait(false)`, so every `await` here resumes on the UI thread's
`SynchronizationContext`, same thread the button-click handler started on.

## Deployment run: the v1.4.2 fix itself failed to parse -- "Incorrect syntax near 'QUOTENAME'" (v1.5.1)

Another real run against `USA-GRANITEHCO-\SQLEXPRESS`, this time deploying
under login `granite` (lowercase), got all the way through data deployment
cleanly again -- 215 batches, 0 failed, initial sync in just over 3
minutes -- and failed only at the very last step, Granite Scheduler
registration, with a new error:

```
Could not register the Granite Scheduler job: Incorrect syntax near
'QUOTENAME'. The data above already deployed successfully -- fix the
issue above and click Start Deployment again once it's resolved.
```

This is from the v1.4.2 fix itself. That fix built the GRANT statement's
target identifier dynamically, entirely in T-SQL: a `DECLARE @existingUser`
lookup, then `EXEC(N'GRANT ... TO ' + QUOTENAME(@existingUser) + N';')` to
run the resulting text. On this server that batch didn't parse. The exact
token the parser choked on wasn't pinned down with certainty -- the
`DECLARE ... = (SELECT ...)` earlier in the same batch is standard,
supported syntax, and `EXEC(string_expression)` wrapping a
concatenation-with-QUOTENAME is an extremely common, normally-valid
pattern, so this may be an edge case of how the two combine in one batch,
or something about this instance's compatibility level -- rather than
spend another real deployment run guessing at the exact cause, the fix
was to stop asking the server to parse a dynamically-assembled GRANT at
all.

`GraniteSchedulerService.cs`'s grant step now does the lookup and
identifier-bracketing in C# instead of T-SQL: `ExecuteScalarAsync` to
check whether the `Granite` login exists, another `ExecuteScalarAsync` to
resolve its actual existing database user name (the same SID-based join
as before, still needed for the v1.4.2 15063 fix), then a plain,
non-dynamic `GRANT EXECUTE ON bi.usp_RunSync TO {bracketedUser};` built
with C# string interpolation -- no `EXEC()`, no `QUOTENAME()`, nothing
for the server to assemble or re-parse. This is the exact same shape
`SqlAgentSchedulerService.cs`'s equivalent method and
`DeploymentRightsService.cs` already use for their own user-existence
checks, neither of which has thrown a syntax error in any real run --
bringing this one in line with a pattern already proven to work rather
than inventing a new one.

## First fully clean run against a real client server (v1.5.1), and a quiet bug it uncovered (v1.5.2)

The very next run against `USA-GRANITEHCO-\SQLEXPRESS` after the v1.5.1
fix went cleanly end to end for the first time: 215 batches executed, 0
failed, the new v1.5.0 skip-sync prompt fired and was accepted (skipped,
since the BI database already had data from 17 minutes earlier), and
Granite Scheduler registration succeeded with no error. Every blocker this
server has produced -- collation, permissions, the single-user stranding,
JobDescription truncation, BI database ownership, SQL error 15063, and the
QUOTENAME syntax error -- has now been hit and fixed at least once.

That clean log still contained something worth chasing: the very first
line logged `Collation check: [GraniteLive_BI] does not exist yet -- it
will be created with [GraniteLive]'s collation`, yet moments later, after
01-08 had all run, the new v1.5.0 check found real synced data already in
`bi.SyncLog` from 17 minutes before this deployment even started. A
database that "doesn't exist yet" can't already contain a 17-minute-old
sync log row from a previous run -- something was misreporting.

`01_Create_BI_Database.sql`'s own existence guard is `IF DB_ID($(BiDb)) IS
NULL`, and it's re-runnable throughout (`IF OBJECT_ID(...) IS NULL` guards
around every `CREATE TABLE`, including `bi.SyncLog`) -- so if
`GraniteLive_BI` genuinely still existed from the previous run (which it
did: that run got all the way through data deployment before only its
Scheduler-registration step failed on the QUOTENAME error), 01 and 02
would correctly no-op and leave the existing data, including that SyncLog
row, untouched. Which is exactly what happened -- 01's own check got it
right. `CollationRepairService`'s check, in `GetCollationAsync`, did not:
it read the BI database's collation with
`DATABASEPROPERTYEX(@db, 'Collation')`, and that function's own
documentation says it returns NULL not only when a database doesn't
exist, but also when the calling login lacks permission to view it. Given
what this project has already found about this specific server -- a
policy that reassigns a freshly created database's ownership away from
its creator (see the v1.4.0 section above) -- it's the likely explanation
here too: something about that same hardening left `GraniteLive_BI`
invisible to `DATABASEPROPERTYEX` for the deployment login, while
`DB_ID()` (which `01_Create_BI_Database.sql` uses, resolving against the
instance-wide `sys.databases` metadata that's visible to every login by
default) saw it fine.

This happened to be harmless on this run only because the collation
already matched from before, so silently skipping the mismatch-repair
branch cost nothing. On a server where it didn't match, the same false
"does not exist yet" reading would skip the repair CollationRepairService
exists to do, and the deployment would go on to fail with the original
`Msg 468` collation-conflict error in `03_Sync_Engine.sql` that this
service was built to prevent in the first place.

Fixed by reading the collation from `sys.databases` directly
(`SELECT collation_name FROM sys.databases WHERE database_id = DB_ID(@db)`)
instead of `DATABASEPROPERTYEX`, bringing this check in line with the
same, more reliable existence check `01_Create_BI_Database.sql` already
relies on.

## Granite Scheduler rejected the interval: SECONDS was never actually confirmed working (v1.5.3)

`GraniteBiSync` registered successfully -- no error from the wizard, job
row created, Status `SCHEDULED` -- but on this Granite install's Scheduled
Jobs screen, `LastExecutionResult` read `Interval value not valid` once
the Scheduler service actually tried to process it (`IntervalFormat =
'SECONDS'`, `Interval = 900` for the 15-minute default).

That `SECONDS` choice traced back to an earlier "confirmed working"
finding that turned out to be too thin -- based on a lighter look at a
real install, not a full comparison. Settled properly this time by
pulling every row from this client's own `dbo.ScheduledJobs`:

| JobName | Interval | IntervalFormat | LastExecutionResult |
| --- | --- | --- | --- |
| InventorySnapshot | `0 0 * * *` | CRON | SUCCESS |
| LightSpeedMasteritemJob | `*/55 9-18 * * *` | CRON | SUCCESS |
| LightSpeedPurchaseOrderJob | `*/8 7-18 * * *` | CRON | SUCCESS |
| LightSpeedTransferJob | `*/3 7-18 * * *` | CRON | SUCCESS |
| LightSpeedSalesOrderJob | `*/8 7-18 * * *` | CRON | SUCCESS |
| LightSpeedItemShopJob | `*/30 7-18 * * *` | CRON | SUCCESS |
| AssignRouteNumbers-Automatically | `10` | MINUTES | SUCCESS |
| GraniteBiSync (this wizard, before this fix) | `900` | SECONDS | **Interval value not valid** |

Of 8 real jobs on this install, none use `SECONDS` at all -- six use
`CRON` (for jobs that only run during business hours, or on a non-uniform
cadence), and one, `AssignRouteNumbers-Automatically`, uses exactly the
shape this job needs: a plain fixed interval, `Interval = 10`,
`IntervalFormat = MINUTES`, scheduled and running with
`LastExecutionResult = SUCCESS`.

`GraniteSchedulerService.cs` now writes `IntervalFormat = 'MINUTES'` with
`context.ScheduleIntervalMinutes` itself as `Interval` -- no seconds
conversion, matching the one directly-proven working precedent rather
than an assumption. The class remarks at the top of the file are updated
with the fuller evidence.

## The window needed dragging bigger on every launch -- traced to a fixed pixel size, not DPI (v1.5.4)

Reported as "still looks the same" / "buttons cut off" against a build
that had never included any Superset code -- ruling that whole extension
out as the cause and pointing straight back at something in the base
wizard that had apparently gone unnoticed since the 900x780 sizing was
first added (v1.4.1). A real screenshot of the running v1.5.3 build on
the actual machine settled it: the window was already filling nearly the
whole screen, with a scrollbar on Step 1's own content -- not a DPI
scaling artifact shrinking a window that had room to grow, but a fixed
`Size = new Size(900, 780)` that simply doesn't leave enough room, on a
smaller-resolution display, for one panel's content plus its own padding
once the header bar, step dots, and button row are subtracted from it.

A first attempt at `AutoScaleMode = AutoScaleMode.Dpi` (confirmed by
this same screenshot evidence not to be the actual cause) made no visible
difference to the sizing, which is what prompted taking an actual
screenshot of the real running app instead of continuing to guess at DPI
theory. It was kept anyway in this build as a "shouldn't hurt" PerMonitorV2
pairing -- see the v1.5.5 entry below for why that turned out to be wrong.

Fixed by sizing the window from `Screen.PrimaryScreen.WorkingArea` instead
of a hardcoded guess -- 85% of the actual available width/height, clamped
between the existing `MinimumSize` (850x700) and a generous cap
(1200x950). Generous on a large monitor, still as large as a small
screen reasonably allows, and adapts to whatever machine the wizard is
run on instead of assuming everyone's screen is at least 900x780. Not yet
re-confirmed on the real machine this was reported from -- check the
window opens filling most of the screen with no scrollbar on Step 1
before trusting this the same way the rest of the sizing code now is.

## `AutoScaleMode.Dpi` clipped button text -- removed, never having fixed anything (v1.5.5)

The v1.5.4 fix above was confirmed working on the real machine: the window
now opens large with no scrollbar. But the same screenshot that confirmed
it also showed something the earlier v1.5.3 screenshot did not -- the
`Test Connection` button on Step 1 rendering with its text visibly
clipped/cut off. The only code difference between the two builds was
`AutoScaleMode = AutoScaleMode.Dpi`, added in v1.5.4 "for correctness"
alongside the `PerMonitorV2` `ApplicationHighDpiMode` already declared in
the .csproj.

This project has no `Designer.cs` -- every control is sized and
positioned by hand in code, with no design-time `AutoScaleDimensions`
baseline for the runtime to scale from. Turning on `AutoScaleMode.Dpi`
against that setup scales control bounds against a baseline that was
never established, which is consistent with a button rendering wide
enough by its `Width` but with its text laid out against a mismatched
scale.

Since this line never fixed the sizing problem it was added for (per the
v1.5.4 evidence above) and now has a real, screenshotted cost with no
offsetting benefit, it's removed rather than patched further. Not yet
re-confirmed on the real machine -- check both of the following on the
next build: the window still opens large with no scrollbar (v1.5.4's
fix, untouched by this change), and `Test Connection` (and the other
buttons) render with full, unclipped text.

## v1.5.5 was wrong: buttons were still clipped after removing AutoScaleMode.Dpi -- the real cause was hardcoded pixel widths (v1.5.6)

Rebuilt and re-tested v1.5.5 on the real machine. Screenshot evidence:
window sizing is still correct (no scrollbar, fills most of the screen),
but `Test Connection` still rendered with clipped text, identical to the
v1.5.4 screenshot -- with `AutoScaleMode.Dpi` already removed. That's a
direct falsification of the v1.5.5 diagnosis: `AutoScaleMode.Dpi` was
never the cause of the button clipping (it also, per the v1.5.4 entry
above, was never the cause of the window-sizing bug either -- it just
happened to be the one thing that changed at the same time as the actual
regression twice in a row, which is why it kept getting the blame).

The real cause: every button in this project (`Test Connection`,
`Refresh`, `Next >`, `Deploy Views to Source Database`, `Start
Deployment`, and every other one) is created with a hardcoded pixel
`Width`, sized by guessing how much room its label text needs at a
default 96 DPI / 100% scale. On a real machine running at a higher
Windows display scale, the OS renders the same font at more physical
pixels than the hardcoded `Width` budgeted for, so the label no longer
fits inside the button and gets clipped -- regardless of `AutoScaleMode`,
because a literal `Width = 150` is a literal 150 pixels whether or not
WinForms is auto-scaling anything else around it.

Fixed properly this time: every hardcoded-width `Button` across
`MainForm.cs` and all six `Step*Control.cs` files now uses `AutoSize =
true` with `AutoSizeMode = AutoSizeMode.GrowOnly` and a `MinimumSize`
carrying the old width as a floor, not a ceiling. Each button now sizes
itself to whatever its actual rendered text needs at whatever DPI it's
actually running at, and never shrinks below its original design width
on a normal display. This is the same "measure the real environment
instead of assuming a fixed number" fix as the v1.5.4 window-sizing
change, applied to the thing that was actually still broken. Not yet
re-confirmed on the real machine -- check that `Test Connection` (and
every other button, not just that one) now shows its full label with no
clipping, at whatever display scale that machine is actually running.

## A deployment run left the BI database stuck in single-user mode -- the collation-repair claim wasn't inside its own safety net (v1.5.7)

Reported directly: the BI database came out of a deployment run set to
single-user, blocking the multi-user reporting login the whole wizard
exists to set up. `CollationRepairService` (see that file's own remarks
for the fuller history) already wraps its SyncLog/COLLATE work in a
`try/finally` that unconditionally runs `SET MULTI_USER` -- added
specifically because an earlier version of this exact bug (v1.2.2)
existed. Reading the code line by line against this new report found the
gap that fix didn't close: the `SET SINGLE_USER WITH ROLLBACK IMMEDIATE`
statement that claims the database ran *before* that try/finally started,
not inside it. If that statement itself throws for any reason (most
plausibly a client-side command timeout on an ALTER DATABASE that had
already completed on the server), the database ends up single-user while
the exception jumps straight to the outer catch, which only logs a
message and never attempts to release it.

Fixed by moving the SINGLE_USER statement inside the same try the rest of
the repair work already runs in, so the finally's `SET MULTI_USER` now
covers it too. This is safe on every ordinary run where the claim
actually succeeds and everything after it works fine: `SET MULTI_USER` on
a database that's already multi-user is a harmless no-op, so wrapping the
claim doesn't change behaviour, it only adds a safety net for the one
statement that didn't have one before.

This is a code-level fix for future runs -- it does not repair a database
that's already stuck. If a BI database is single-user right now from a
run before this fix, release it directly:

```sql
ALTER DATABASE [GraniteWMS_BI] SET MULTI_USER;
```

(substitute the actual BI database name). Not yet re-confirmed against a
real deployment run -- the next full run should be watched end to end to
confirm the database comes out multi-user regardless of how the
collation check itself goes.

## The v1.5.7 fix was confirmed not to leave the database stuck -- but a real run showed the collation claim can still be refused outright, root-caused to an idle SSMS window (v1.5.8)

The very next real run after v1.5.7 finished cleanly -- 211 batches,
scheduler registered, database not left single-user -- but the collation
check itself failed before it could do anything: `ALTER DATABASE failed
because a lock could not be placed on database 'GraniteLive_BI'. Try
again later.` The same exact error then hit `01_Create_BI_Database.sql`'s
own `ALTER DATABASE` independently a few seconds later, which ruled out
a bug in this project's own connection handling -- something external
genuinely had the database open. Asked directly, the cause was confirmed
rather than guessed at: an SSMS query window was sitting open on
`GraniteLive_BI` with an earlier manual ALTER script still in it.

Because the collation check couldn't run, the mismatch it exists to
catch went unrepaired, and `03_Sync_Engine.sql` immediately hit the
original "Cannot resolve the collation conflict" errors this whole
service was built to prevent (3 of the run's 4 failed batches).

`SET SINGLE_USER WITH ROLLBACK IMMEDIATE` is supposed to forcibly
disconnect a session like that idle SSMS window, but SQL Server can
still refuse the request outright with `Msg 5061` -- and that error's
own text says "Try again later," i.e. Microsoft's documented shape for
this is a transient condition worth retrying, not a hard failure. Fixed
by retrying the SINGLE_USER claim specifically on Msg 5061, up to 4
attempts, 5 seconds apart, before giving up. If every attempt is still
refused, the error message now says plainly that another connection
(an open SSMS window, Superset, another copy of this wizard) is holding
the database open and needs to be closed, instead of just relaying SQL
Server's generic wording. Not yet re-confirmed against a real deployment
run with something actually holding the database open at the time.

## Panel 1's SQL username field offers other logins on the picked server as suggestions (v1.6.0)

Requested directly: once a SQL Server/instance is picked, suggest the SQL
logins that exist on it instead of leaving the username field a blank box
the user has to already know the answer for. The field is now an editable
dropdown (`ComboBox`, `DropDownStyle.DropDown` -- never `DropDownList`,
so typing a name that isn't in the list, including one that doesn't exist
yet for the "create a dedicated login" bootstrap flow below it, still
works exactly as before) rather than a plain `TextBox`.

New `Core/SqlLoginDiscovery.cs` lists the server's SQL-authenticated
logins (`sys.sql_logins`, excluding disabled logins and the internal
`##...##` certificate-mapped ones) using a connection authenticated as
the *current Windows identity* -- the same pattern Panel 1's own "I don't
have a login with database-creation rights" bootstrap flow already uses
(`BootstrapLoginService`), not the deployment connection itself, which
per `SqlConnectionFactory`'s own remarks never falls back to Windows auth
under any circumstance. This is a separate, read-only, throwaway
connection that exists purely to suggest names. Only SQL logins are
listed, never Windows logins or groups -- the username field only ever
feeds a SQL-authenticated deployment connection, so a Windows account
name would never actually work there.

Deliberately best-effort end to end: if the current Windows account
can't connect to the picked server at all, or can connect but isn't
privileged enough to see other logins (a non-admin login typically only
sees its own row in `sys.server_principals`), this comes back with an
empty list and the field just stays a normal box to type into -- no
error shown anywhere. Triggered when the server field is picked from the
list or left (tabbed/clicked away from) after typing, not on every
keystroke, and once more from `OnEnter` to cover arriving on Panel 1 with
a server already set (Back from a later panel). Not yet tested against a
real server -- worth confirming both that a privileged Windows account
does see the expected logins, and that an unprivileged one degrades to a
plain empty-suggestions box with no visible error.

## What "Start Deployment" actually does

1. Opens one connection (SQL-authenticated, per Panel 1) to `master`.
2. For each ticked script file, in natural filename order: reads it,
   replaces `$(SourceDb)`/`$(BiDb)`, splits it into batches on `GO`
   boundaries, and runs each batch. A batch that is nothing but a
   standalone `USE [db];` line isn't sent to the server as text — the
   wizard calls `SqlConnection.ChangeDatabase` instead, so subsequent
   batches run in the right database.
3. Runs `EXEC bi.usp_RunSync;` once so the BI tables are populated
   immediately, rather than sitting empty until the first scheduled tick.
4. Writes `Run_BI_Sync.bat` into the script folder — it calls
   `sqlcmd -S <server> -d <BiDb> -E -Q "EXEC bi.usp_RunSync;"`. `-E` is
   trusted/integrated auth, so no SQL password is ever written to disk;
   whichever Windows account the scheduled task runs as is what
   authenticates.
5. Registers (or replaces) a Windows Scheduled Task — not a SQL Server
   Agent job, so this works the same way on Express as on Standard/
   Enterprise — via the `TaskScheduler` library (Microsoft.Win32.TaskScheduler,
   by David Hall). It's a one-time trigger repeating on the chosen interval
   indefinitely (`Repetition.Duration = TimeSpan.Zero`), with
   `ExecAction.WorkingDirectory` set to the script folder so it never
   defaults to `System32`.

A batch that fails doesn't stop the run — the scripts are written to be
re-runnable, and this mirrors that (e.g. the SQL Agent script failing on an
Express instance with no Agent service shouldn't block everything else).
Failures are counted and shown in the log and the final status line.

## Script selection (Panel 4) and why some files start unticked

Looking at the actual script set in `Source Files/Granite BI DB/`, three
kinds of file don't belong in an automated run the same way `01`–`08` do:

- **`00_Deploy_All.sql`** uses `:setvar` and `:r` — SQLCMD scripting
  directives that only `sqlcmd.exe` (or SSMS in SQLCMD mode) can execute.
  The wizard's native GO-batch engine can't run these, so any file
  containing them is detected automatically, shown disabled
  (checkbox locked to indeterminate), and skipped with a warning in the
  log rather than crashing on the first unresolvable `:r` line.
- **`00_Fix_Collation.sql`** is a one-off remediation script for a specific
  collation-mismatch error, not part of the deploy order (it's also not in
  `00_Deploy_All.sql`'s own `:r` list). It starts unticked but can be
  ticked on if needed.
- **`06_Scheduler_SqlAgent.sql`** sets up a SQL Server Agent job, which the
  script's own header comment calls "Optional on SQL Server Standard...
  On Express (no Agent) use the Windows Task Scheduler wrapper instead."
  Panel 5 *is* that Task Scheduler wrapper, so this file starts unticked
  to avoid registering the sync twice through two different mechanisms.
  It can still be ticked on for a Standard/Enterprise instance that should
  have both.

This is a judgment call beyond the literal brief ("read script file arrays
natively") worth flagging: rather than hardcode these three filenames, the
wizard detects the SQLCMD-directive case generically (so it won't crash on
whatever future master script gets added), and only the "optional/
superseded" default-unticked suggestion is name-based. Every file is always
listed and, except for the truly unsupported ones, always overridable.

## Where the code deviates from the brief, and why

**`Context Connection=False`.** The brief's "bypass session caching" rule
asks for this exact connection-string keyword. It's a real SQL Server
keyword, but it belongs to the legacy, SQL-CLR-hosted `System.Data.SqlClient`
in-process provider (a stored-procedure-assembly scenario) — it has no
meaning for an external client, and `Microsoft.Data.SqlClient` (the driver
the brief also specifies) doesn't define it at all. Setting it would either
fail to compile against `SqlConnectionStringBuilder`, or throw "Keyword not
supported" at runtime if forced into the string as raw text. `Core/SqlConnectionFactory.cs`
implements the actual intent — every connection is a brand-new,
explicitly SQL-authenticated session, never satisfied by a cached one —
using `IntegratedSecurity = false` (never falls back to Windows auth) and
`Pooling = false` (every `Open()` is a genuinely new physical connection).

**Bugs caught by manual review** (the GUI project couldn't be compiled in
this sandbox, so these were only caught by reading the code closely, not by
a compiler):

- The GO-boundary regex anchors `$` per line. Several of the deployment
  scripts are CRLF, and a trailing `\r` before every `\n` stopped `GO\r\n`
  from ever matching as a separator line — silently collapsing a file into
  one giant unrunnable batch. Fixed by normalizing all line endings to `\n`
  before splitting (`ScriptBatchParser.NormalizeLineEndings`).
- `Control.BeginInvoke(Delegate)` was called with a bare lambda in two
  places (the log console and the script checklist). A lambda can't
  convert to the non-specific `System.Delegate` type — only to a concrete
  delegate type — so this would not have compiled. Fixed by wrapping both
  in `new MethodInvoker(...)`.

**Bugs caught by actually running the engine** against the real script
files in `LogicHarness/` (see "Verified" below) — these are the ones
review missed:

- **A commented-out sample block could have executed for real.**
  `05_Security_Roles_Logins.sql` has a `/* ... */` block with several `GO`
  lines inside it (a sample login the client is meant to edit and
  uncomment by hand), including a real `CREATE LOGIN [client_bi] WITH
  PASSWORD = N'CHANGE_ME_Strong!Passw0rd'` statement. The GO-splitter
  doesn't know about comments, so it was cutting batches *inside* that
  block — one of which was exactly that `CREATE LOGIN` statement, with
  nothing to stop it from being sent to SQL Server as-is. Fixed by adding
  `ScriptBatchParser.StripCommentsPreservingLayout`, which blanks out every
  comment (line and nested block, string-literal-aware so `$(SourceDb)`
  inside `N'...'` survives) before batches are ever cut, run right after
  line-ending normalization and before variable substitution. The harness
  asserts directly that no executable batch ever contains `CREATE LOGIN`
  or the sample password.
- **The default-unticked heuristic never actually fired for two of the
  three files it names.** `ScriptFileScanner` strips underscores from the
  filename before checking it against the hint list (`"00fixcollation.sql"`
  instead of `"00_fix_collation.sql"`), but the hints themselves —
  `"fix_collation"`, `"deploy_all"` — still had underscores in them, so the
  substring check could never match. Only `"schedulersqlagent"` (no
  underscore to begin with) happened to work. `00_Fix_Collation.sql` was
  starting ticked, not unticked as documented above and in the Panel 4
  hint text. Fixed by normalizing the hints the same way as the filename.

## Next phase: Superset deployment automation (planning, 2026-08-27)

With the BI database side working end to end, the next piece is automating
the Superset side: pointing Superset at the new BI database and loading the
25 dbo.vw_BI_* datasets, ~25 charts and 5 cadence dashboards that already
exist as a ready-to-use Superset "Import Assets" bundle in
`Source Files/Superset [Dataset-Charts-Dashboard]/`. This section records
the research and decisions from that planning pass; no code from this
section exists yet.

**What's already solved technically, not just documented.** The asset
bundles (`GraniteWMS_Superset_Assets.zip` and friends) follow Superset's
native import format: a `metadata.yaml`, one `databases/GraniteWMS.yaml`
(a single connection entry with a fixed UUID and a masked password), and
`datasets/`, `charts/`, `dashboards/` YAMLs that all bind to that UUID
rather than by name. Superset also has a REST API
(`POST /api/v1/security/login` for a JWT, `GET /api/v1/security/csrf_token/`,
then `POST /api/v1/assets/import/` or the resource-specific
`/api/v1/database/import/` etc.) that accepts that same zip plus a
`passwords` field supplying the masked database's real password
programmatically. That means the vendor installation guide's Step 7
("import via the Superset UI, get prompted for the password interactively")
is the documented manual path, not a hard technical requirement — the whole
import can be one authenticated API call. Exact request field names still
need to be confirmed against the Superset version actually deployed at a
client site before this gets built.

**What can be automated as a report, not a decision.** The companion
reporting-plan document's "consultant confirmation checklist" (actual
Transaction.Type / Document.Type values in use, whether cost or expiry data
is populated, whether ERP integration is live, single- vs. multi-site, stock
take approval workflow in use, pickface levels maintained) is, item for
item, something a SQL query can answer rather than something a consultant
has to go ask about. The plan is to build one read-only diagnostic script
covering the whole checklist, in the same spirit as `Check_Sync_Log_Health.sql`
below, so a consultant gets a table of findings instead of working through
the checklist by hand.

**What still needs a human.** A couple of checklist items are real business
decisions, not unknowns to look up (e.g. whether reversed transactions should
be excluded from throughput figures, how tight to set per-dataset cache
timeouts for a given client's data volume). Those stay as choices someone
makes, not things a script resolves.

**Architecture decision: separate tool, not a new step in this wizard.**
Considered extending this wizard with new steps after Step 6, versus a
second, separate tool. Went with **separate for now**: this wizard talks
to the local SQL Server and Windows Task Scheduler, while the Superset side
is a remote HTTP API call to a different (often Linux) host doing something
conceptually different — and the working, tested BI deployment flow above
shouldn't carry any risk from actively-developed Superset-side code. The
plan is to build the new Superset tool sharing this project's coding
conventions (same `Core/`-style service classes, same log-console UX) so it
can be folded into a single combined wizard later if that turns out to be
the better long-term shape, without a rewrite.

**Proposed shape for the Superset tool**, to build next:
1. Create the `superset_bi` (and optional `client_bi`) SQL logins — plain
   T-SQL, could reuse this wizard's existing script-runner pattern.
2. Create or repoint the Superset database connection via the API, using
   the constructed `mssql+pyodbc://...` URI for the BI database.
3. Import the asset bundle via the API, supplying the password
   programmatically instead of prompting.
4. Run the full readiness/diagnostic report (see above) and show it in a
   log console, same style as this wizard's Step 6.
5. A lightweight post-import smoke test via the API (dataset/chart/dashboard
   counts match the bundle; a couple of chart data-endpoints return without
   error) in place of a human eyeballing the Superset UI.

## Verified: `LogicHarness/`

A separate, tiny console project with **zero NuGet packages** — it links
the real source files directly from `GraniteBiDeployWizard/Models/` and
`Core/` (not copies; edits to one show up in the other), skipping only the
three files that need `Microsoft.Data.SqlClient` or `TaskScheduler`
(`SqlConnectionFactory.cs`, `DeploymentRunner.cs`, `ScheduledTaskService.cs`)
and everything WinForms (`UI/`, `MainForm.cs`, `Program.cs`). Because it has
no external dependencies, it compiles and runs in any sandbox with a plain
`dotnet run`, no network required.

It was run against your actual 10 files in `Source Files/Granite BI DB/`
and asserts, for real, against real content — not fixtures: that the
scanner finds and classifies all 10 files correctly (00_Deploy_All.sql
locked out, 00_Fix_Collation.sql and 06_Scheduler_SqlAgent.sql unticked,
the rest ticked); that GO-splitting produces more than one batch per file
regardless of whether that file is CRLF or LF on disk (03 and 08 turned out
to be plain LF, an unplanned but useful extra case); that no `$(SourceDb)`/
`$(BiDb)` token survives into an executable batch; that `USE [$(BiDb)]`
resolves to the real target database name; that the commented-out sample
login in 05 never produces an executable `CREATE LOGIN` batch; and that the
generated `Run_BI_Sync.bat` has the right `-E`/`-d`/`EXEC bi.usp_RunSync`
content and contains neither password. All 218 checks pass as of this
writing. Run it yourself with:

```
cd LogicHarness
dotnet run -c Release
```

(if you point `scriptFolder` in `Program.cs` at a different path, it'll
verify against whatever script set you give it.)

This does **not** verify the SQL execution, task scheduling, or any GUI
code — those still need the full project built on Windows.

## Files

```
GraniteBiDeployWizard/
  GraniteBiDeployWizard.csproj
  app.manifest
  Program.cs
  MainForm.cs                      wizard shell: step navigation, progress dots
  Models/
    DeploymentContext.cs           all wizard state, shared across panels
    ScriptFileItem.cs               one discovered .sql file + include/skip state
    LogEntry.cs                     one log line (level, message, timestamp)
  Core/
    SqlConnectionFactory.cs         connection string + Test Connection
    ScriptBatchParser.cs            $(...) substitution, GO split, USE detection
    ScriptFileScanner.cs            enumerates + classifies the script folder
    NaturalFileNameComparer.cs      "01, 02, ... 10" ordering, not "1, 10, 2"
    PathHelper.cs                   trailing-backslash normalization
    DeploymentRunner.cs             executes the selected scripts end to end
    BatchFileGenerator.cs           writes Run_BI_Sync.bat
    ScheduledTaskService.cs         registers the Windows Scheduled Task
    SqlAgentSchedulerService.cs     registers the SQL Server Agent job (the Panel 5 alternative)
    GraniteSchedulerService.cs     registers the Granite Scheduler job (the Panel 5 alternative)
    DeploymentRightsService.cs      Panel 6's permanent full-rights grant for the deployment login
    BootstrapLoginService.cs        Panel 1/5 dedicated-login + grant flows
    SourceViewsPrerequisiteService.cs  checks/deploys the source DB's dbo.vw_BI_* views
    CollationRepairService.cs      realigns the BI database's collation to the source's
    WindowsAccountNameResolver.cs   ".\name" -> "COMPUTERNAME\name" for SQL/Task Scheduler
    WindowsCredentialValidator.cs   LogonUser-based upfront password check (Panel 5)
    SqlInstanceDiscovery.cs         local registry + network SQL instance discovery
    WindowsAccountDiscovery.cs      local Windows account discovery (Panel 5)
  UI/
    WizardStepControl.cs            base class for the 6 panels
    Step1CredentialsControl.cs
    Step2ReportingViewsControl.cs   checks/deploys the source DB's dbo.vw_BI_* views
    Step2DatabasesControl.cs
    Step3ScriptFolderControl.cs
    Step4ScheduleControl.cs
    Step5DeployLogControl.cs

LogicHarness/                        standalone, dependency-free engine verification (see above)
  LogicHarness.csproj                 links the real Models/Core files, no PackageReferences
  NuGet.Config                        clears package sources so restore never touches the network
  Program.cs                          runs the engine against a real script folder and asserts on it

docs/
  GraniteWMS_BI_Deployment_Wizard_Manual.docx   branded operator manual with per-step screenshots

ops/
  Check_Sync_Log_Health.sql           read-only bi.SyncLog health check, see below
```
