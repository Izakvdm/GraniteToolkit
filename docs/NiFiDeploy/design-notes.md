# NiFiDeploy: GraniteWMS CSV import kit

A standard, client-agnostic Apache NiFi setup for loading data into GraniteWMS.
A client system drops a CSV into a folder, NiFi loads it into a staging table,
and a stored procedure maps it into Granite with full validation and audit.

Adding a new data type needs SQL only (a staging table and a proc). The NiFi
flow does not change.

> **Installing at a client: use the Granite Toolkit.** Open the GraniteWMS Toolkit
> and choose **Deploy NiFi integration**. That wizard does steps 2 to 5 below in one
> go (NiFi, the service, the SQL objects, NiFi's SQL login and the flow). The manual
> steps here are the fallback, and the reference for what the wizard does.
>
> The wizard carries its own copies of `sql\01` to `05` and `nifi\GraniteCsvImport.json`
> (in `GraniteToolkit\src\GraniteNiFiDeploy\Resources`). When you change one here,
> copy it there too and rebuild the toolkit.

```
NiFiDeploy\
  README.md                     this runbook
  install\Install-NiFi.ps1      optional: installs NiFi as a Windows service
  nifi\GraniteCsvImport.json    the NiFi flow (import into the canvas)
  sql\01_Framework.sql          log table, wrapper procs, security role
  sql\02..05_Feed_*.sql         MasterItem, TradingPartner, SalesOrder, PurchaseOrder
  sql\09_NiFi_SqlLogin.sql      optional: least-privilege SQL login for NiFi
  sql\99_Remove_Old_Design.sql  optional: clean-up where the first version ran
  sql\_Template_Feed.sql        starting point for a new feed
  samples\*.csv                 one test file per feed
```

---

## How it works

```
Inbound\<Feed>\file.csv
   |  NiFi: pick up (file must be 10 sec old), copy to Archive\<Feed>\<yyyy-MM>\
   |        strip Excel BOM
   v
[ one file at a time ]
   1. EXEC Custom_NiFi_ClearStaging      removes leftovers
   2. CSV  -> dbo.Custom_<Feed>Staging   header names = column names
   3. EXEC Custom_NiFi_RunImport         stamps rows, calls Custom_Import<Feed>,
                                         writes dbo.Custom_NiFiImportLog
   v
SUCCESS -> done   |   REJECTED (data problem) -> reason in the log, warning bulletin in NiFi
                  |   SQL unreachable -> retries about an hour, then Error\<Feed>\
```

- **The feed is the folder name.** `Inbound\SalesOrder\x.csv` runs `Custom_ImportSalesOrder`.
  Files placed directly in `Inbound`, in nested folders, or not ending `.csv` are ignored.
- **Everything is logged in one table**, `dbo.Custom_NiFiImportLog`: file, status,
  message and row counts. Use it for a Granite Data Grid.
- **A bad file is rejected whole.** Nothing is half-applied.
- **Every received file is kept** in `Archive`, named `yyyyMMdd-HHmmss-SSS_<original>.csv`.
  To re-run a file, copy it back into its `Inbound\<Feed>` folder.

---

## Install at a client

### 1. Prerequisites

| Item | Notes |
|---|---|
| Windows Server with SQL Server access | NiFi can sit on the Granite app or SQL server |
| NiFi 2.x `nifi-*-bin.zip` | https://nifi.apache.org/download/ |
| JDK zip, Windows x64 | Java 21 or 25 **LTS** recommended (NiFi 2 needs 21+) |
| NSSM zip | https://nssm.cc/download |
| Microsoft JDBC Driver for SQL Server zip | `sqljdbc_*.zip` |
| 2 to 4 GB free RAM | default heap is 1 GB |

Put the four zips in one folder (the installer looks in `NiFiDeploy\media` by default).

### 2. Install NiFi

**Option A, script (recommended).** Elevated PowerShell:

```powershell
cd NiFiDeploy\install
.\Install-NiFi.ps1 -MediaPath D:\NiFiMedia
```

It prompts for the NiFi admin password (12+ characters) and does the Granite
doc steps: extract, `jdk` folder, `nifi-env.cmd`, `nifi.cmd` patch, port,
login, JDBC driver to `C:\nifi\drivers`, NSSM service, import folders.
Useful switches: `-Port 9443`, `-HeapSize 2g`, `-ImportRoot E:\GraniteImport`,
`-Username`, `-ServiceName`, `-Force` (reinstall).

**Option B, manual.** Follow https://granitewms.github.io/GraniteDocs/7.0/tools/nifi/, then also:
- set the login: `bin\nifi.cmd set-single-user-credentials <user> <password>`
- copy `mssql-jdbc-*.jre11.jar` to `C:\nifi\drivers`
- create `C:\GraniteImport\Inbound\<Feed>` for each feed, plus `Archive` and `Error`

Browse to `https://localhost:8443/nifi` (accept the self-signed certificate).

### 3. Deploy the SQL

In SSMS, **select the client's Granite database**, then run in order:

1. `01_Framework.sql`
2. `02_Feed_MasterItem.sql` to `05_Feed_PurchaseOrder.sql` (only the feeds the client uses)
3. `09_NiFi_SqlLogin.sql`: set the password at the top first

All scripts are safe to re-run. There is no `USE` statement, so check the database selector.

**Before the first order import**, confirm the document defaults on
`Custom_ImportSalesOrder` / `Custom_ImportPurchaseOrder` (`@DocumentType`,
`@DocumentStatus`, `@Site`) against `SELECT DISTINCT [Type],[Status],Site FROM dbo.Document`.
Wrong values import cleanly but the documents never show in WebDesktop.

### 4. Import the flow

1. In NiFi, drag a **Process Group** onto the canvas, click the upload icon, choose
   `nifi\GraniteCsvImport.json`.
2. Right-click the group > **Parameters** (or the hamburger menu > Parameter Contexts >
   *Granite CSV Import*) and set:

| Parameter | Example |
|---|---|
| `granite.db.url` | `jdbc:sqlserver://SQLSERVER01:1433;databaseName=GraniteLive;encrypt=true;trustServerCertificate=true` |
| `granite.db.user` | `svc_granite_nifi` |
| `granite.db.password` | (sensitive, never exported) |
| `granite.jdbc.driver.dir` | `C:\nifi\drivers` |
| `import.inbound.dir` / `archive` / `error` | `C:\GraniteImport\Inbound` etc. |
| `import.min.file.age` | `10 sec` (raise it if files arrive slowly over the network) |

For a named SQL instance use `jdbc:sqlserver://SERVER;instanceName=INSTANCE;databaseName=...` (SQL Browser running) or the instance's fixed port.

3. Right-click the group > **Enable all controller services**, then **Start**.

### 5. Test

Copy the files in `samples\` into the matching `Inbound\<Feed>` folders, MasterItem
and TradingPartner first. Then:

```sql
SELECT TOP (20) * FROM dbo.Custom_NiFiImportLog ORDER BY Id DESC;
```

| What you see | Meaning |
|---|---|
| `SUCCESS` with counts | Imported |
| `ERROR`, ErrorNumber 50002 | Data rule failed, message lists the rows. Fix the file and re-drop it |
| `ERROR`, ErrorNumber 50020 | CSV did not fit the staging table (wrong header or data type) |
| `ERROR`, 50011 / 50012 | Feed folder exists but its SQL was not deployed |
| Nothing logged, file in `Error\<Feed>` | NiFi could not reach SQL Server. Check the bulletin and parameters |

---

## Add a new feed (works orders, stock on hand, ...)

1. Copy `sql\_Template_Feed.sql` to `sql\NN_Feed_<Feed>.sql` and replace `ZZFeed` with
   the feed name (letters, digits, underscore).
2. Staging columns = CSV header names. Keep `StagingId` and `ImportId`.
3. Write the validation and mapping in the proc. Keep the OUTPUT parameters.
   `02_Feed_MasterItem.sql` is the reference for upserts with audit, and
   `04_Feed_SalesOrder.sql` for header/line documents.
4. Run it, create `Inbound\<Feed>`, drop a test file. No NiFi change.

## Day-to-day

- **Monitoring:** `Custom_NiFiImportLog`, or a Granite Data Grid over it. In NiFi, rejected
  files raise a yellow bulletin and database errors a red one.
- **Order of files:** one file at a time, oldest first. When master data and orders
  arrive together, the master data must reach the folder first.
- **Housekeeping:** staging clears itself. Rows from failed imports are kept 14 days for
  troubleshooting. Archive is never purged by NiFi, so add a scheduled clean-up if needed.
- **Upgrading NiFi:** install the new version alongside with `-ServiceName` / `-Port`,
  then download the flow from the old one (right-click > Download flow definition) and upload it.

## Security notes

- The NiFi password is never written anywhere in plain text. Keep it in the client's
  password vault, not in this folder.
- NiFi listens on `localhost` only by default. Remote access needs `nifi.web.https.host`
  and `nifi.web.proxy.host` set in `conf\nifi.properties`, plus a firewall rule.
- The SQL login (`09_NiFi_SqlLogin.sql`) can only insert into staging and run the import
  procs. The procs run as owner, so NiFi has no direct rights on Granite tables.

## What changed from the first version

| Was | Now |
|---|---|
| 4 flows, one per feed, with copy-paste errors (wrong log table names) | 1 generic flow, feed taken from the folder |
| 4 log tables, `Message varchar(250)` truncating 2000-char errors | 1 log table, 2000 chars, row counts |
| File name concatenated into SQL (breaks on `'`, injection risk) | Passed as a parameter |
| Files moved to Consumed before processing, overwritten on name clash | Archived with a timestamp prefix, never overwritten |
| Failures auto-terminated, file stuck at STARTED | Every outcome logged, DB errors retried then sent to Error |
| Two files could mix in staging | One file at a time, rows stamped per import |
| Order lines numbered in arbitrary order | Line numbers follow file order |
| Hard-coded `USE [GraniteBestBeforeTest]`, paths and DB in each flow | Run against any DB, settings in one parameter context |
| Credentials in a README | No credentials stored |
