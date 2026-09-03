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
- Skip each packet's **"SSIS semantics you must preserve"** and **"Respond with"** sections —
  identical boilerplate repeated in every packet.
- The exact seam signature (return type, parameter types, class, namespace) is already quoted
  in the packet's own reason text. **Do not open the generated `.cs` file to look it up.**

Only open a generated file if a packet is genuinely ambiguous.

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

Mapping rules from the packets: `Dts.Events.FireError` + `TaskResult = Failure` → **throw**;
`FireInformation` → an `ILogger`; `Dts.Variables[...]` → `ctx.Variables`;
`AcquireConnection` for SQL → `ctx.Uow`. A script that stamped `DateTime.Now` per row should
normally use `ctx.LoadedAtUtc` — but **call that out**, it changes the stored values.

If a task's real logic cannot be reproduced against these abstractions, **say so plainly
instead of inventing a substitute.**

## Step 3 — apply and build

Resolve `$ssisx` / `$dotnet` via the bootstrap block in `ssisx-extract.prompt.md` (Step 1), then:

```powershell
& $ssisx apply-fills --out out --fills fills-library
& $dotnet build out/generate/Generated.slnx -c Debug --nologo
```

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
