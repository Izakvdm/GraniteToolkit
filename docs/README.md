# Module documents

Manuals, release notes and the design notes each module built up before it moved into the toolkit (2026-10-01). The code lives in `src/<Module>`; these are reference only and describe each module up to the version it had when it moved. Newer changes are in the main README's "Changes in this version".

| Folder | What's in it |
|---|---|
| `InstallWizard` | Operator manual v0.5.1 (Word, PDF); design notes and version history up to v0.5.1 |
| `BiDeploy` | Operator manual v1.6.0 (Word, PDF); release notes v1.5.4 to v1.6.0; design notes and history; `Check_Sync_Log_Health.sql` |
| `DbSwitcher` | Design notes and history up to v0.1.0 (how installs and database versions are detected) |
| `NiFiDeploy` | Design notes for the CSV import framework; the four sample CSVs for testing each feed; the feed template, the SQL login script and the clean-up script for the old design (not deployed by the wizard) |

The NiFi feed SQL and the flow that the wizard deploys are in `src/GraniteNiFiDeploy/Resources`. That copy is the one to change.
