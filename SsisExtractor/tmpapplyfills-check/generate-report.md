# ssisx generate -- report

Phase 3 of the `ssisx generate` plan: one runnable C# project per package, written under `generate/<Package>/`, plus Directory.Build.props/Directory.Packages.props/Shared/appsettings.Shared.json/Generated.slnx written once under `generate/`. Every generated file carries no hand-editing expectation -- regenerate freely; anything a human needs to add belongs in the package's own appsettings.json or a sibling file this tool never writes.

**19 file(s) written across 1 package(s), 5 gap(s).**

| Package | Files written | Starter tests | Gaps |
|---|---|---|---|
| SyntheticScriptComponentSeams | 11 | 8 | 4 |

## Gaps -- not silently dropped, each has a reason

Each gap carries a stable `GapId` and a tier. **Tier 1/2 gaps have a work packet** under
`gaps/<Package>/` -- a self-contained brief you can paste into a chat window. **Tier 3 gaps
deliberately do not**: those are missing tool support, and closing them per-package by hand
would hide one systemic emitter gap behind N one-off patches. See `gaps.json` for the index.

| Package | Tier | GapId | Reason |
|---|---|---|---|
| SyntheticScriptComponentSeams | advisory | `GAP:SyntheticScriptComponentSeams:DFT_Load` | OLE DB Source 'OLE DB Source' uses a SqlCommand -- generated code assumes each result-set column is named/aliased exactly like the pipeline's own buffer column name; verify before running. This tool never rewrites the author's own SQL. |
| SyntheticScriptComponentSeams | 2 -- logic (packet) | `SCRIPT-COLUMN:SyntheticScriptComponentSeams:SyntheticScriptSeamsTarget.SCR_Cleanse` | Script Component 'SCR_Cleanse' produces 2 column(s) (FullName, IsValid) -- emitted as a single `private partial SCR_CleanseResult SCR_Cleanse(SyntheticScriptSeamsTargetSqlRow row, in RowContext ctx)` seam. The project will not compile (CS8795) until a second part of 'SyntheticScriptSeamsTargetTransform' implements it; apply one with `ssisx apply-fills`. |
| SyntheticScriptComponentSeams | 1 -- datum (packet) | `TEST-ORACLE:SyntheticScriptComponentSeams:SyntheticScriptSeamsTarget.SCR_Cleanse` | Script Component 'SCR_Cleanse' (SCR_Cleanse) has no generated test at all (a filled seam is human logic -- see the TEST-ORACLE work packet to write one). |
| SyntheticScriptComponentSeams | advisory | `GAP:SyntheticScriptComponentSeams:SyntheticScriptComponentSeams.Notification` | no notification wiring was generated (IPackageResultNotifier/AddEmailNotifications) -- a .dtsx carries no notification-recipient information at all, so this is opt-in; re-run with --notifications once real recipients/SMTP settings exist |
| _(shared)_ | advisory | `Etl.Core` | this generator does not produce Etl.Core -- it is hand-written shared plumbing (SqlBulkCopy wrapper, CSV reader, host, email notifier), not derivable from any .dtsx. Pass --etl-core <path> to copy it in as part of this command, or copy the Etl.Core folder shipped alongside this tool (Tools/Etl.Core, a sibling of Tools/SsisExtractor) to generate/Etl.Core/ by hand before building. |

## Packages that failed to load -- not generated at all

_None._
