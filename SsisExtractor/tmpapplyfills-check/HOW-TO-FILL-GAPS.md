# How to fill the gaps in this generated output

Read this before touching anything under `generate/`. This file was written by
`ssisx generate` itself, fresh, every run -- it will be overwritten next time, same as
everything else under this output folder except the fills directory (see below).

## The fills directory for THIS run -- read this even if you've done this before

`--fills` was resolved to:

    D:\PoC\SSIS\Tools\SsisExtractor\tmpapplyfills-check\fills

**This is the DEFAULT location, and it is INSIDE this disposable `--out` folder.**
Anything you write here (a Tier-2 `.cs` fill, a Tier-1 `.decisions.json`) will be
permanently lost the next time someone deletes or regenerates this `--out` folder from
scratch -- this has already happened for real, more than once, and is exactly why this
warning exists. **Before writing any fill, re-run `generate` with an explicit `--fills`
pointing OUTSIDE this `--out` folder** -- e.g. `--fills ..\fills-library` (a sibling of
`--out`, not inside it), or ask where the durable fills location for this engagement is
if you don't know. Do not write fills into the path above as-is.

## Where things are, relative to THIS file

- `gaps/<Package>/<GapId>.md` -- auto-generated work packets, one per open gap. Read the
  exact one you're working on before writing anything -- it has the real question or the
  real script source, not a paraphrase.
- `D:\PoC\SSIS\Tools\SsisExtractor\tmpapplyfills-check\fills\<Package>\*.cs` and `D:\PoC\SSIS\Tools\SsisExtractor\tmpapplyfills-check\fills\<Package>.decisions.json` -- where YOUR
  answer goes (the exact path printed above, not a generic `fills/`). **These are empty
  right now on purpose.** `ssisx` never writes here, ever -- so an empty folder, or
  `apply-fills` reporting "0 fills applied", means nobody has answered a packet yet. It
  does not mean the packets are missing -- check `gaps/`. If files you expect to be here
  are genuinely gone, do NOT re-port everything from scratch before checking whether they
  still exist somewhere else (e.g. committed in git, or under an old `--out`'s own
  `fills/` from before this path was made explicit).
- `generate/<Package>/` -- the generated C# project. **Never hand-edit files here** --
  regenerated/overwritten on every `ssisx generate` run. A Script Task/Component's unfilled
  logic shows up here as an unimplemented `partial` method (a Script Component's own
  combined method, named after the component, or a Script Task's
  `RunScriptAsync`) that deliberately fails to build (`CS8795`) until you supply the other
  half in the fills directory above -- that failure is intentional, not something to patch
  around here.
- `generate-report.md` / `gaps.json` -- the full gap list, with every `GapId` and tier.

## 2 gap(s) waiting for an answer, right now

| Package | Tier | Kind | GapId | Work packet |
|---|---|---|---|---|
| SyntheticScriptComponentSeams | 2 -- logic | ScriptComponentColumn | `SCRIPT-COLUMN:SyntheticScriptComponentSeams:SyntheticScriptSeamsTarget.SCR_Cleanse` | `SCRIPT-COLUMN_SyntheticScriptComponentSeams_SyntheticScriptSeamsTarget.SCR_Cleanse.md` |
| SyntheticScriptComponentSeams | 1 -- datum | TestOracle | `TEST-ORACLE:SyntheticScriptComponentSeams:SyntheticScriptSeamsTarget.SCR_Cleanse` | `TEST-ORACLE_SyntheticScriptComponentSeams_SyntheticScriptSeamsTarget.SCR_Cleanse.md` |

## What to actually do for each tier

**Tier 1 is one workflow with THREE different answer shapes -- check the Kind column above
before writing anything, they are not interchangeable:**

- **`LookupJoinKey` / `EncryptedConnectionManagerSecret`** (a missing FACT, or an
  acknowledgment): open the work packet, read its exact question, do **not** guess a
  confident-sounding answer -- confirm it with a human if you're not certain. Write the
  answer into `D:\PoC\SSIS\Tools\SsisExtractor\tmpapplyfills-check\fills\<Package>.decisions.json`, in the exact JSON shape the packet
  shows, with a real `ConfirmedBy`.
- **`TestOracle`** (a starter test the deterministic emitter could not itself derive --
  a Conditional Split case, or a test for a filled Script Task/Component seam): the packet
  asks for a WHOLE new xUnit test file, not a value in a JSON file. Write it under
  `D:\PoC\SSIS\Tools\SsisExtractor\tmpapplyfills-check\fills\<Package>\Tests\<Name>Tests.cs`, with this comment as the file's own FIRST
  line (not above a seam -- there is no seam here, this is a brand-new file):
  ```
  // ssisx-fill: GapId=<exact GapId> Author=<you> Date=<yyyy-mm-dd> EvidenceSha256=<from the packet>
  ```
- **`LocalFileSourceData`** (a realistic sample file to replace the synthetic Tier-A
  placeholder): the packet names the EXACT file name to use (its own "Save as" line --
  do not guess one). Write it under `D:\PoC\SSIS\Tools\SsisExtractor\tmpapplyfills-check\fills\<Package>\TestData\<that exact name>`,
  with no comment of any kind (a data file has no provenance convention -- see the packet's
  own contract for why).

**Tier 2 (a Script Task/Component whose real source IS in the packet, just needs
porting to C#):** read the packet -- it includes the actual original script text and the
exact seam signature you must match (do not rename or reshape it). Write the `.cs` file it
describes under `D:\PoC\SSIS\Tools\SsisExtractor\tmpapplyfills-check\fills\<Package>\`, with this comment immediately above the seam,
copying `EvidenceSha256` from the packet verbatim:

```
// ssisx-fill: GapId=<exact GapId from the table above> Author=<you> Date=<yyyy-mm-dd> EvidenceSha256=<from the packet>
```

## After writing a fill

Run `ssisx apply-fills --out <this folder> --fills "D:\PoC\SSIS\Tools\SsisExtractor\tmpapplyfills-check\fills"` -- the `--fills` value MUST match the path printed above
exactly, or your fill will not be found. This validates and copies your fill into
`generate/<Package>/Fills/`, and reports anything still outstanding or stale. `ssisx.exe`
lives under `Tools/SsisExtractor/src/Ssis.Extract.Cli/bin/Debug/net8.0/` relative to
wherever this was generated FROM -- if you're working from this output folder alone and
don't have that path, ask where the `Tools/` folder is before assuming it isn't
available; do not skip `apply-fills` and hand-copy a file into `generate/` instead, since
that bypasses the staleness/provenance check it exists to provide.

## Adding more tests -- NOT a gap, do this separately

Once a package builds clean with zero gaps left above, you can still raise coverage past
the deterministic starter tests -- this is voluntary enrichment, never listed in
`gaps.json` and never affecting whether a package counts as generatable. Read
`generate/<Package>/README.md`'s own "Test coverage notes" section (not the whole
`.Tests` project) for what to add, write it under `D:\PoC\SSIS\Tools\SsisExtractor\tmpapplyfills-check\fills\<Package>\MoreTests\`
(a `// ssisx-more-test: Author=... Date=... Targets=...` first-line comment is audit-only,
never required), then apply with `ssisx apply-tests --out <this folder> --fills
"D:\PoC\SSIS\Tools\SsisExtractor\tmpapplyfills-check\fills"`. See `Docs/AI-Test-Enrichment-Plan.md` for the full design.

## One rule that applies regardless of tier

Do not attempt to build/run this project against a REAL database, or invent a throwaway one,
to "verify" a fill -- none is available in this environment. `dotnet build` succeeding is
the expected extent of checking here (this does not apply to a `LocalFileSourceData` fill --
supplying a realistic sample FILE is precisely what that gap kind asks for; it is the
no-server, no-database `dotnet test --filter Category!=Integration` tier this concerns).
