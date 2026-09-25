---
mode: agent
description: Port Tier-2 script seams into fills-library, apply them, and build. Reads packets per component, not per gap.
---

# ssisx: fill Tier-2 gaps

- **Package:** `${input:package:Package name, e.g. the folder name under out/gaps/}`

## Step 0 — check for existing fills FIRST

List `fills-library/<package>/`. If files already exist, **read and verify them against the
work packets — never overwrite them.** Prior, already-reviewed work may be sitting there.
If they are sound, skip straight to Step 3.

Also check `out/fills/<package>/` — if real work is stranded in that disposable location, move
it into `fills-library/<package>/` and re-apply.

## Step 1 — read the packets economically

Work packets live in `out/gaps/<package>/`. They are written **per gap**, but their evidence
is **per script component/task**:

- Every gap produced by the same Script Component shares the **same script source** and the
  **same `EvidenceSha256`**. Read **one packet per component/task, not one per gap.**
- **Never open/read a packet whole.** Its `"SSIS semantics you must preserve"` and
  `"Respond with"` sections are identical boilerplate (~25 lines) repeated verbatim in every
  packet from the same run — reading them more than once is pure waste. Extract only the
  `## Why ssisx generate stopped here` through `### <script source>` sections, e.g.:
  ```powershell
  sed -n '/^# Work packet/,/^## SSIS semantics/p' out/gaps/<package>/<file>.md
  ```
  (stop the range one line before `## SSIS semantics` — everything from there to the end of
  the file is the boilerplate to skip).
- The exact seam signature (return type, parameter types, class, namespace) is already quoted
  in the packet's own reason text. **Do not open the generated `.cs` file to look it up.**
- **Exception, do open the generated file for this:** before writing `using`s, grep the
  generated row/entity type file(s) the packet references (e.g. `grep -n "^namespace"
  out/generate/<Package>/{Sql,Csv,Model}/<Type>.cs`) for their real namespace — guessing wrong
  here is only caught by a full build, costing an entire extra build+test cycle.

Only open a generated file for the reason above, or if a packet is genuinely ambiguous.

## Step 2 — write the fills

Known-good `Etl.Core` API — **do not read `Etl.Core/` to confirm any of this**:

```
RowContext(long RowNumber, DateTime LoadedAtUtc, string SourceName)
LoadContext(Guid RunId, DateTime StartedAtUtc, string PackageName)
ScriptTaskContext(IUnitOfWork Uow, LoadContext Load, PackageVariables Variables,
                  IServiceProvider Services)

PackageVariables : Get<T>(name, fallback) | GetRequired<T>(name) | TryGet<T>(name, out v)
                 | Set(name, value) | Contains(name)          // names include "User::" prefix

IUnitOfWork      : Context (DbContext)
                 | ExecuteSqlAsync(sql, ct)
                 | ExecuteSqlAsync(sql, object?[] parameters, ct)   // EF "{0}" placeholders
                 | BulkInsertAsync(...) | ExecuteSqlWithoutTransactionAsync(sql, ct)

File paths       : IOptions<FileSourceOptions>.Value["<Name>"].ResolvedPath
Logging          : ctx.Services.GetRequiredService<ILogger<T>>()
```

Generated projects build with `Nullable=enable` **and `TreatWarningsAsErrors=true`** — a fill
that would only warn elsewhere fails the build here.

**File convention:** one file per generated **class**, not per gap:

```
fills-library/<package>/<ClassName>.Fills.cs
```

Each file is a second `partial` part of the generated class, in the **exact** namespace and
class name from the packet. Put one attribution line immediately above each seam:

```
// ssisx-fill: GapId=<from packet> Author=<name> Date=<yyyy-mm-dd> EvidenceSha256=<from packet>
```

Copy the `GapId` and `EvidenceSha256` **verbatim** from the packet — never compute or guess them.
Fields (a compiled `Regex`, a constant, a helper) are allowed, since this is a class part.

Mapping rules from the packets: `Dts.Events.FireError` + `TaskResult = Failure` → **throw** (this
rewrite's own documented equivalent of a Script Task `Failure` result — an unhandled exception
triggers the same rollback + failure-handler path a `Value=Failure` constraint/`OnError` handler
would, so treat this as settled, not a guess needing a human check); `FireInformation` → an
`ILogger`; `Dts.Variables[...]` → `ctx.Variables`; `AcquireConnection` for SQL → `ctx.Uow`.

**`DateTime.Now` needs different treatment depending on WHERE it's called:**
- **Inside a Script Component's per-row method** (the seam itself runs once per row, exactly
  where `Input0_ProcessInputRow` ran) — call `DateTime.UtcNow` directly INSIDE that method. It
  still evaluates fresh per row; only the local→UTC timezone changes, which you should still call
  out. Reach for `ctx.LoadedAtUtc` only when the original value was clearly meant to be one shared
  instant for the whole load (e.g. reproducing `GETUTCDATE()`) — using it as a substitute for a
  per-row `DateTime.Now` changes the actual stored values on every row and must be called out, not
  substituted silently.
- **Inside a Script Task** (no per-row loop at all) — there is no per-call "now" on `ctx`;
  `ctx.Load.StartedAtUtc` is fixed at the whole run's start and would collapse an elapsed-time
  computation. Use `DateTime.UtcNow` directly there too, and call out the local→UTC change.

If a task's real logic cannot be reproduced against these abstractions, **say so plainly
instead of inventing a substitute.**

## Step 3 — apply and build

Resolve `$ssisx` / `$dotnet` via the bootstrap block in `ssisx-extract.prompt.md` (Step 1) —
**reuse that same resolved path here; never `dotnet run --project ...` for the CLI**, which
pays a full restore/build check on every call — then:

```powershell
& $ssisx apply-fills --out out --fills fills-library
& $dotnet build out/generate/Generated.slnx -c Debug --nologo -v minimal
```

Read only the final `Build succeeded`/`Build FAILED` line plus any `error CS...` lines — do
not tail the full restore/compile transcript into context.

`CS8795` means a seam is still unimplemented — check `apply-fills` output before assuming a
port needs redoing. A build is the furthest verification that is in scope: **never** invent
connection strings, seed data, or sample input files to make something "runnable".

## Step 4 — report

- Confirm the build result and which seams were applied.
- List every **non-mechanical port decision** for human review (timestamp semantics,
  connection-manager → config-key inferences, query restructuring, transaction differences).
- List remaining **Tier-1** gaps — these still need a human.
- Remind me to `git add fills-library && git commit`; an uncommitted fill can still be lost.

## Logging

Append to `session-logs/<yyyy-MM-dd>-session.md`: this prompt, the commands, their output, the
port decisions, and a bump of the counters.
