# GraniteWMS BI Deployment Wizard — Release Notes

**Since v1.5.3** (last version uploaded to the Granite Dropbox folder) **through v1.6.0**

---

## v1.5.4 — Window now sized to fit any screen

The wizard's window was a fixed 900×780, which on a smaller-resolution laptop display left Step 1's own content needing an internal scrollbar and the window needing to be dragged bigger by hand on every launch. It now sizes itself off the actual monitor's available space instead (85% of it, within sensible limits), so it opens comfortably filling the screen on any display.

## v1.5.6 — Button text no longer cut off

Every button in the wizard — Test Connection, Next, Deploy Views to Source Database, Start Deployment, and the rest — now sizes itself to its own label instead of a fixed pixel width, so text renders in full regardless of the machine's Windows display scaling.

## v1.5.7 – v1.5.8 — Fixed: BI database could be left in single-user mode

A deployment run could, in some cases, leave the BI database stuck in single-user mode afterward, blocking normal multi-user reporting access. Root-caused to the automatic collation-repair step that runs at the start of every deployment, and fixed in two parts:

- The database is now reliably released back to multi-user, even if the repair step itself fails partway through.
- If another connection (an open SSMS window, Superset, another copy of the wizard) is briefly holding the BI database open at the moment of a deployment, the wizard now retries a few times before giving up, and reports plainly what to close if it still can't get in — instead of failing outright on the first attempt.

If a database is already stuck single-user from a run on an older build, it needs releasing once by hand:
```sql
ALTER DATABASE [YourBiDbName] SET MULTI_USER;
```

## v1.6.0 — SQL username suggestions on Step 1

Once a SQL Server/instance is selected on Step 1, the SQL username field now offers a dropdown of that server's existing SQL logins as suggestions (checked using the current Windows identity), instead of a blank box you have to already know the answer for. It's still freely editable — typing any name directly, including one that doesn't exist yet for the "create a dedicated login" option, works exactly as before.

---

### Not included in this build

The Superset deployment extension explored this week (a full second wizard flow covering Superset installs end to end) is being kept as a separate module for now, per a deliberate decision to keep it off the main build until it's ready on its own. It isn't part of v1.6.0.
