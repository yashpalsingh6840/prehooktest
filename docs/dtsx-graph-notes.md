# `.dtsx` XML and generated-C# empirical notes — control-flow/data-flow graph shapes (Gate 2)

Compiled from direct reading of the 5 canonical `.dtsx` files under
`D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\` (`Package.dtsx`,
`Package_Advanced.dtsx`, `Package_Exports.dtsx`, `Package_Legacy.dtsx`,
`Package_Transforms.dtsx`) and the generated C# under
`D:\tmp\Sandeep_Packages\gen\generate\<PackageName>\<PackageName>.cs` (plus a look at
`Fills/` and `ScriptTasks/` for one package, to confirm a Script Task's call-graph shape).
No file under `extract/`, `gen/gaps*`, `svk/`, `coverage-report.md`, `generate-report.md`,
any `README.md` inside a `generate/<PackageName>/` folder (self-authored by the generation
tool about its own output), or any `bin/`/`obj/` folder was read — see the independence rule
in [SSIS-Rewrite-Validation-Plan.md](../SSIS-Rewrite-Validation-Plan.md). This document only
covers Gate 2 (control-flow/data-flow graph parity); see
[dtsx-xml-notes.md](dtsx-xml-notes.md) for Gate 1 (SQL/schema) findings, some of which are
referenced here where they intersect (disabled nodes, event handlers).

This is empirical evidence, not a spec. Sample-specific values (task/component/package names)
appear only as illustrative snippets — extraction logic must key off the structural shapes
described here, never off these specific names.

## 1. Precedence-constraint XML shape (control-flow edges)

### 1.1 Attribute shape

A `<DTS:PrecedenceConstraint>` element (real example, from `Package_Advanced.dtsx`, an
expression-gated constraint):

```xml
<DTS:PrecedenceConstraint
  DTS:refId="Package.PrecedenceConstraints[{F670C0B3-4F2F-483B-888E-9734113959BA\}]"
  DTS:CreationName=""
  DTS:DTSID="{F670C0B3-4F2F-483B-888E-9734113959BA}"
  DTS:EvalOp="3"
  DTS:Expression="@[User::RowsLoaded] &gt; 0"
  DTS:From="Package\SCR_NotifyAndLogProgress"
  DTS:LogicalAnd="True"
  DTS:ObjectName="{F670C0B3-4F2F-483B-888E-9734113959BA}"
  DTS:To="Package\DFT_BuildSalesSummary" />
```

A plain (unconditional, success-only) constraint has no `EvalOp`/`Expression`/`Value` at all:

```xml
<DTS:PrecedenceConstraint
  DTS:refId="Package.PrecedenceConstraints[PC_ScriptTask_to_Load]"
  DTS:CreationName=""
  DTS:DTSID="{5f6af062-d1d8-4d7a-a563-90f15dba3884}"
  DTS:From="Package\SCR_ValidateAndLogStart"
  DTS:LogicalAnd="True"
  DTS:ObjectName="PC_ScriptTask_to_Load"
  DTS:To="Package\DFT_LoadCustomers" />
```

A failure-only constraint (`Package.dtsx`, edge into the failure-handling task):

```xml
<DTS:PrecedenceConstraint
  DTS:refId="Package.PrecedenceConstraints[{10BA4886-AEA2-492E-9AC6-18C7FB4CB224\}]"
  DTS:From="Package\DFT_LoadCustomers"
  DTS:LogicalAnd="True"
  DTS:ObjectName="{10BA4886-AEA2-492E-9AC6-18C7FB4CB224}"
  DTS:To="Package\SQL_LogLoadFailure"
  DTS:Value="1" />
```

Attributes observed, by role:

- **`DTS:From` / `DTS:To`** — the edge endpoints. These are **not** `DTS:DTSID` GUIDs and not
  bare names: they are the **same backslash-path `refId` string** used to identify the source
  and target `<DTS:Executable>` (e.g. `Package\DFT_LoadCustomers`,
  `Package\SEQ_Prepare\FST_ArchiveWorkbook`). Resolve edges by matching this path string against
  each executable's own `DTS:refId`, not by DTSID lookup.
- **`DTS:refId`** (of the constraint itself) — either a designer-assigned friendly name in
  brackets (`Package.PrecedenceConstraints[PC_Load_to_Notify]`, when the designer user
  renamed the constraint) or the constraint's own GUID in brackets
  (`Package.PrecedenceConstraints[{0E6B1065-...}]`, the default when never renamed). `DTS:DTSID`
  is always present as a separate GUID attribute regardless. `DTS:ObjectName` mirrors whichever
  form appears inside the `refId` brackets (friendly name or GUID-as-string).
- **`DTS:Value`** — present only for a **constraint-based** predicate (success/failure/
  completion); its value `1` was observed with `DTS:To` pointing at a failure-handling task,
  consistent with a "Failure" predicate. No `Value="0"`/completion-value instance was observed in
  any of the 5 samples — that predicate value remains unconfirmed empirically (Microsoft's docs
  describe `Success`/`Failure`/`Completion` as the three constraint values; only two show up here:
  the unmarked default, which every plain success-only edge above uses, and `1` for failure).
- **`DTS:EvalOp`** + **`DTS:Expression`** — present only for expression-gated constraints.
  `EvalOp="3"` was the only value observed, paired with a real SSIS expression string
  (`@[User::RowsLoaded] &gt; 0`) — an "expression or constraint" style evaluation per Microsoft's
  documented `EvalOp` enum. No plain `EvalOp="1"` (expression-only, no constraint) or
  `EvalOp="2"` (expression-and-constraint) instance was observed in these 5 samples.
- **`DTS:LogicalAnd`** — `"True"` on every single constraint observed. This attribute matters
  only when **multiple** constraints converge on the same `To` node (AND vs. OR combination of
  multiple incoming edges) — no sample had two or more incoming constraints on the same node, so
  the `LogicalAnd="False"` (OR) case is unobserved and must degrade explicitly if encountered.
- **`DTS:CreationName`** is always the empty string `""` on every constraint in all 5 samples —
  it is not a type discriminator for constraints (unlike its role on `<DTS:Executable>`).

### 1.2 Constraint scope: declared under the container that holds both endpoints

Precedence constraints are **not** all listed in one flat package-level place. Each
`<DTS:Executable>` container (the package root itself, and any Sequence/ForEach Loop container)
that has ≥2 children needing an ordering relationship carries its **own**
`<DTS:PrecedenceConstraints>` block, scoped to constraints whose `From`/`To` are both children of
that same container. Real example from `Package_Advanced.dtsx` — the `SEQ_Prepare` Sequence
Container has its own nested block for its two children:

```xml
<DTS:Executable DTS:refId="Package\SEQ_Prepare" DTS:CreationName="STOCK:SEQUENCE" ...>
  <DTS:Executables>
    <DTS:Executable DTS:refId="Package\SEQ_Prepare\FST_ArchiveWorkbook" .../>
    <DTS:Executable DTS:refId="Package\SEQ_Prepare\SQL_TruncateTargets" .../>
  </DTS:Executables>
  <DTS:PrecedenceConstraints>
    <DTS:PrecedenceConstraint
      DTS:refId="Package\SEQ_Prepare.PrecedenceConstraints[{6CFFB78C-...\}]"
      DTS:From="Package\SEQ_Prepare\SQL_TruncateTargets"
      DTS:LogicalAnd="True"
      DTS:To="Package\SEQ_Prepare\FST_ArchiveWorkbook" />
  </DTS:PrecedenceConstraints>
</DTS:Executable>
```

...while the **package root's own** `<DTS:PrecedenceConstraints>` block (a sibling of the root's
`<DTS:Executables>`) separately governs ordering *between* the Sequence Container (as a single
opaque node) and its siblings — it references `Package\SEQ_Prepare` as a whole `From`, never
reaching inside it. So graph-building must walk the executable hierarchy recursively and pair each
container's own `<DTS:PrecedenceConstraints>` block with the direct children declared in that same
container's `<DTS:Executables>` — a flat single-pass scan of every `PrecedenceConstraint` in the
document, without tracking which container it lives under, would be structurally correct for edge
list purposes (the `From`/`To` path strings are already globally unique) but would lose the
information needed to compute concurrency groups correctly per-container (see §3).

A container with only one child never has a `<DTS:PrecedenceConstraints>` block at all — no
ordering is needed. Confirmed on `FEL_SampleFiles` (ForEach Loop containing a single
`SQL_LogFileName` task, `Package_Advanced.dtsx`) and on the `OnError` `<DTS:EventHandler>` in the
same file (single task, no constraints block) — absence of the block does not mean "not yet
inspected," it structurally means "nothing to order."

### 1.3 Isolated node vs. "not yet examined" — not structurally distinguishable in isolation

Nothing in the XML flags a node as "intentionally unordered." A node that appears in nobody's
`From`/`To` within its container's constraint scope is indistinguishable, by inspecting that one
node alone, from a node whose constraints simply haven't been read yet. The only way to tell is by
building the full set of `From`/`To` references for that container and then computing set
difference against that container's full child list — a node absent from both sets is a genuine
concurrency/root candidate, not a data-entry gap. Confirmed concretely in `Package_Exports.dtsx`:
of its 4 top-level executables, only one constraint exists in the whole file
(`SQL_TruncateExportLog → DFT_AdoNetRoundTrip`); `DFT_ExportDelimited` and `DFT_ExportFixedWidth`
never appear as a `From` or `To` anywhere — they are true root/no-constraint nodes, confirmed only
by exhaustive absence, not by any positive marker.

### 1.4 Disabled node: still carries a real incoming edge

Confirmed again on `Package_Legacy.dtsx`'s `SQL_LegacyStep_DISABLED`
(`DTS:Disabled="True"`, per the Gate 1 notes): the package-level `<DTS:PrecedenceConstraints>`
block still contains a live edge into it —

```xml
<DTS:PrecedenceConstraint
  DTS:From="Package\SQL_AtomicSwap"
  DTS:LogicalAnd="True"
  DTS:To="Package\SQL_LegacyStep_DISABLED" />
```

— even though the node never executes and the generated C# has zero call site or class for it
(per Gate 1 notes §1.1). Gate 2's left-graph extraction must decide explicitly how to represent
this: excluding the disabled node (and its edge) from the comparable node/edge set while still
reporting it as an accounted-for `DisabledOnLeft` finding, consistent with the Gate 1 rule, rather
than letting it surface as a spurious `LeftOnly` node or edge.

### 1.5 Event-handler scope: a fully separate executable/constraint namespace

`Package_Advanced.dtsx` is the only sample with an `<DTS:EventHandlers>` block (`OnError`, at
package scope). Its `refId` namespace is disjoint from the main control flow —
`Package.EventHandlers[OnError]\SQL_LogOnError` — with its **own** `<DTS:Executables>` (this
sample's handler has exactly one task, so no `<DTS:PrecedenceConstraints>` block is present, per
§1.2). A handler with 2+ tasks would need its own precedence-constraint scope exactly like a
Sequence Container's, by the same rule. This is structurally distinct from the **inline**
failure-predicate constraint pattern in §1.1/1.4 (a normal `DTS:To` edge with `DTS:Value="1"`
inside the *main* control flow) — both represent "what runs on failure," but only the
`<DTS:EventHandlers>` form is SSIS's actual `OnError`/`OnFailure` event mechanism (which can fire
from any descendant executable, not just the one edge's source); the inline `Value="1"` edge is a
plain conditional successor limited to its own `From` node. Gate 2 extraction must treat these as
two different edge/node categories, not conflate them.

## 2. Data-flow path XML shape

Inside a `Microsoft.Pipeline`'s `<pipeline>` element (nested under
`<DTS:Executable>/<DTS:ObjectData>`), after the `<components>` list, a `<paths>` container holds
one `<path>` element per data-flow edge. Real example (`Package_Transforms.dtsx`):

```xml
<paths>
  <path
    refId="Package\DFT_DerivedAndSplit.Paths[Data Conversion Output]"
    endId="Package\DFT_DerivedAndSplit\DER_Enrich.Inputs[Derived Column Input]"
    name="Data Conversion Output"
    startId="Package\DFT_DerivedAndSplit\DCONV_Types.Outputs[Data Conversion Output]" />
</paths>
```

- **`startId`** / **`endId`** — reference a specific named **output**/**input** of a specific
  component, using the shape `<DFT-refId>\<ComponentObjectName>.Outputs[<OutputName>]` /
  `...Inputs[<InputName>]` — i.e. component identity plus which named output/input port,
  not just component-to-component. A component with multiple outputs (Conditional Split,
  Multicast) produces multiple `<path>` elements from the same `startId` component but different
  `Outputs[...]` names — this is the XML-level equivalent of data-flow "fan-out."
- **`refId`** of the path itself follows the same bracketed-name convention as executables/
  constraints: `<DFT-refId>.Paths[<PathName>]`.
- **`name`** is the human-assigned or default path name (often duplicating the output name).
- Unlike control-flow `PrecedenceConstraint`, there is no expression/predicate on a data-flow
  path — a path is unconditional; branching logic (Conditional Split) lives in the **component's**
  own properties (a routing expression per output), not on the path element.

## 3. Concurrency groups actually observed

Computed from scratch per §1.3's method (child set minus every `From`/`To` reference in that
container's own constraint scope), confirmed against the generated C#:

| Package | Concurrent nodes (no ordering constraint between them) | Container scope |
|---|---|---|
| `Package_Advanced` | `DFT_FlagCustomers`, `DFT_ExcelImport`, `FEL_SampleFiles` (all three become successors of `SEQ_Prepare` completing; no constraint exists *between* any pair of the three) | Package root |
| `Package_Exports` | `DFT_ExportDelimited`, `DFT_ExportFixedWidth`, `SQL_TruncateExportLog` (all three are true root nodes — nothing points into or out of them relative to each other; `SQL_TruncateExportLog` additionally has its own downstream edge to `DFT_AdoNetRoundTrip`) | Package root |
| `Package` | None — the 5 top-level executables form a single chain with one conditional branch (the `RowsLoaded > 0` gate) and one failure branch; both branches still have exactly one `From` | Package root |
| `Package_Legacy` | None — 3-node chain plus one disabled-node edge | Package root |
| `Package_Transforms` | None — confirmed by reading every package-root-scope `PrecedenceConstraint` (7 constraints, 8 nodes, forming one linear chain: `SQL_TruncateEnriched → DFT_DerivedAndSplit → SQL_TruncateRegionSummary → DFT_LookupAndAggregate → SQL_TruncateWithContact → DFT_SortAndMergeJoin → SQL_TruncateMerged → DFT_MergeSortedBranches`) | Package root |

No explicit XML parallelism marker exists anywhere in any of the 5 samples. Specifically checked
and **not found**: a package- or container-level `MaxConcurrentExecutables`/`IsolationLevel`
override, or any `DTS:ThreadHint` value other than the defaults `"0"` (the overwhelming majority)
and `"1"` (exactly two executables total, one each in `Package.dtsx` and `Package_Legacy.dtsx` —
both otherwise-ordinary Execute SQL Tasks in the middle of a strictly linear chain with no sibling
to run alongside). `ThreadHint` is therefore **not** a usable concurrency signal in these samples —
concurrency must be computed purely from the absence-of-constraint method in §1.3, never read off
a `ThreadHint` value.

## 4. Generated C# call-graph shapes observed

### 4.1 Sequential calls — the default shape

Every package's `RunAsync` method is a flat `partial class <PackageName>Package` with a
`public async Task<int> RunAsync(CancellationToken ct)` that, inside one `try` wrapping a single
`IUnitOfWork` transaction, issues a strictly ordered list of `await <ExecutableObjectName>(uow,
ct);` calls — one call per top-level control-flow node, in the same order as the precedence-
constraint chain. Real example (`Package_Transforms.cs`, matching the 8-node chain in §3 exactly):

```csharp
await SQL_TruncateEnriched(uow, ct);
await DFT_DerivedAndSplit(uow, ct);
await SQL_TruncateRegionSummary(uow, ct);
await DFT_LookupAndAggregate(uow, ct);
await SQL_TruncateWithContact(uow, ct);
await DFT_SortAndMergeJoin(uow, ct);
await SQL_TruncateMerged(uow, ct);
await DFT_MergeSortedBranches(uow, ct);
```

Each such method (`internal async Task <Name>(IUnitOfWork uow, CancellationToken ct)`) builds a
concrete step object (`ExecuteSqlStep`, `DataFlowStep<TIn,TOut>`, `ConditionalSplitStep<T>`,
`MulticastStep<T>`, `ForEachLoopStep`, `FileSystemStep`, `SecondaryConnectionSqlStep`, or a
Script Task class) and passes it to a single shared helper:

```csharp
private async Task Step(ILoadTask task, IUnitOfWork uow, CancellationToken ct) =>
    _results.Add(await task.RunAsync(uow, _load, ct));
```

Every one of these step types implements a common `ILoadTask.RunAsync(...)` — this is the
generated "unit of work" concept for Gate 2's right-graph nodes: one node per top-level
control-flow executable, called exactly once via `Step(...)`, regardless of which concrete step
class it is.

### 4.2 Expression-gated edge → `if`/`else` reproducing the SSIS expression, with an explicit skip record

The `@[User::RowsLoaded] &gt; 0` expression-gated constraint from §1.1 (`Package.dtsx`) becomes an
`if`/`else` around the guarded call, with the **literal original SSIS expression string** embedded
in the generated log message — a strong, checkable provenance link back to the exact
`DTS:Expression` value on the left:

```csharp
// Guard: @[User::RowsLoaded] > 0
if ((packageVariables.GetRequired<int>("User::RowsLoaded") > 0))
{
    await DFT_BuildSalesSummary(uow, ct);
}
else
{
    Log<PackagePackage>().LogInformation(
        "{Step}: skipped -- its precedence constraint's condition ({Condition}) evaluated false",
        "DFT_BuildSalesSummary", "@[User::RowsLoaded] > 0");
    _results.Add(new StepResult("DFT_BuildSalesSummary", 0, 0, TimeSpan.Zero) { Skipped = true });
}
```

### 4.3 Explicit parallelism construct — `Task.WhenAll`, present in 2 of 5 samples

Contrary to an initial assumption that no sample might exercise real parallelism, **both**
packages identified as having a concurrency group in §3 (`Package_Advanced`, `Package_Exports`)
generate a real `Task.WhenAll(...)` over per-branch `RunBranchAsync(...)` calls — one call per
concurrent node, each running in its **own** `IServiceProvider` scope and its **own**
`IUnitOfWork`/transaction (`RunBranchAsync` begins, runs, commits or rolls back independently).
Real example (`Package_Exports.cs`), including a code comment that **names the concurrency
group and states the reason** (no ordering constraint) directly above it:

```csharp
// Concurrent (3 executables -- SSIS ran these with no ordering constraint between them):
//   - DFT_ExportDelimited
//   - DFT_ExportFixedWidth
//   - SQL_TruncateExportLog
await uow.CommitAsync(ct);
uowActive = false;
await Task.WhenAll(
    RunBranchAsync(async branchUow => { await DFT_ExportDelimited(branchUow, ct); }, ct),
    RunBranchAsync(async branchUow => { await DFT_ExportFixedWidth(branchUow, ct); }, ct),
    RunBranchAsync(async branchUow => { await SQL_TruncateExportLog(branchUow, ct); }, ct)
);
```

The subsequent single-successor edge (`SQL_TruncateExportLog → DFT_AdoNetRoundTrip`) is marked
with its own edge-naming comment: `// Flow 3: SQL_TruncateExportLog → DFT_AdoNetRoundTrip` before
the plain `await DFT_AdoNetRoundTrip(uow, ct);` call — a second, edge-level (not just
group-level) naming convention worth using for reverse-mapping (§4.5), though per the independence
rule and the Gate 1 notes' warning about trusting generated comments, this comment is evidence to
**cross-check** against the independently-computed left-side concurrency grouping, never a
substitute for computing that grouping from the XML.

Outside these two `Task.WhenAll` sites, no other explicit-parallelism construct
(`Parallel.ForEachAsync`, `Task.Run`, manual thread/task management) was found anywhere in any of
the 5 packages' top-level `.cs` files.

### 4.4 Container representation

- **Sequence Container** (`SEQ_Prepare`, `Package_Advanced`) becomes its own
  `internal async Task SEQ_Prepare(IUnitOfWork uow, CancellationToken ct)` method containing a
  flat, ordered list of `await <Child>(uow, ct);` calls for its children — i.e. structurally
  identical in shape to the top-level `RunAsync` body, just scoped to the container's own children
  and given the container's own name as the method name:
  ```csharp
  internal async Task SEQ_Prepare(IUnitOfWork uow, CancellationToken ct)
  {
      await SQL_TruncateTargets(uow, ct);
      await FST_ArchiveWorkbook(uow, ct);
  }
  ```
  The outer `RunAsync` calls this container method as a single `await SEQ_Prepare(uow, ct);` — the
  container is one opaque node from the outer graph's perspective, exactly mirroring the left-side
  XML nesting in §1.2.
- **ForEach Loop Container** (`FEL_SampleFiles`, `Package_Advanced`) is **not** rendered as a
  visible C# loop construct (no `foreach`/`while` at the orchestration level). It becomes a single
  method that builds one `ForEachLoopStep` object, handing the loop mechanics (file enumeration)
  to a runtime helper class and the per-iteration body to a delegate:
  ```csharp
  internal async Task FEL_SampleFiles(IUnitOfWork uow, CancellationToken ct)
  {
      var step = new ForEachLoopStep(
          "FEL_SampleFiles",
          new ForEachFileLoopAction(File("FEL_SampleFiles"), "*.csv", false, ForEachFileNameMode.NameAndExtension),
          currentFile => SQL_LogFileNameStatement.BuildStatement(currentFile),
          Log<ForEachLoopStep>());
      await Step(step, uow, ct);
  }
  ```
  Consequence for Gate 2: the ForEach Loop's single child task does **not** get its own top-level
  `await` call or its own method the way a Sequence Container's children do — it is fully absorbed
  into the lambda passed to `ForEachLoopStep`. A structural node-count comparison must account for
  this: one left-side container + one left-side child collapses to exactly one right-side call
  site, not two.
- **Data-flow components inside a single `Microsoft.Pipeline`** (sources, transforms,
  Conditional Split/Multicast branches, Merge Join, Lookup) never get their own top-level
  `await`-called method. They are all constructed as **nested expressions/constructor arguments**
  inside the one method for their owning Data Flow Task — e.g. a Conditional Split's branches
  become a `ConditionalSplitBranch<...>[]` array literal inside the `ConditionalSplitStep<T>`
  constructor call, and a Merge Join's two sources become two **local functions**
  (`IRowSource<...> BuildLeft() {...}` / `BuildRight() {...}`) called inline
  (`Package_Transforms.cs`, `MRG_CustomerContacts()`). This means Gate 2's right-graph "node per
  data-flow component" claim (per the plan's §4 wording) does not hold at the call-site level for
  data-flow internals — only the **owning Data Flow Task** is a call-graph node; the components and
  paths inside it must be recovered from the constructor-argument/local-function structure, not
  from separate `await` sites.

### 4.5 Failure/error-handler path representation

- The package-level `OnError` **event handler** (`Package_Advanced`, §1.5) becomes inline code
  **inside the outer `catch (Exception ex)` block** of `RunAsync` — not a separate method, not a
  call to a generated class for the handler's task. The handler's SQL is inlined directly as a
  raw string argument to `uow.ExecuteSqlWithoutTransactionAsync(...)`, wrapped in its own nested
  `try`/`catch` so a failing handler doesn't mask the original exception, and its logical name is
  recorded only as a plain string added to a `handlersRun` list:
  ```csharp
  catch (Exception ex)
  {
      ...
      var handlersRun = new List<string>();
      try
      {
          await uow.ExecuteSqlWithoutTransactionAsync(
              "INSERT INTO dbo.EtlRunLog (...) VALUES (N'Package_Advanced', N'(event handler)', N'OnError', N'An error was raised.');",
              CancellationToken.None);
          handlersRun.Add("SQL_LogOnError");
      }
      catch (Exception hEx) { logger.LogError(hEx, "...", PackageName, "SQL_LogOnError"); }
      ...
  }
  ```
  This is a fundamentally different shape from a normal task node: `SQL_LogOnError` gets **no**
  dedicated method, **no** `ExecuteSqlStep`/`Step(...)` call, and is invisible to any extraction
  that only looks for `await <Name>(...)` call sites in `RunAsync`'s main body — it must be
  recovered from inside the `catch` block specifically.
- The inline **failure-predicate edge** (§1.1/1.4 `Value="1"` pattern, `Package.dtsx`'s
  `DFT_LoadCustomers → SQL_LogLoadFailure`) is represented the **same way** — inlined inside the
  outer `catch` block, hardcoded SQL string, `handlersRun.Add("SQL_LogLoadFailure")` — even though
  structurally on the left it is a normal `<DTS:PrecedenceConstraint>` in the main
  `<DTS:PrecedenceConstraints>` block (not an `<DTS:EventHandlers>` entry). The generated code does
  not distinguish the two left-side mechanisms (inline `Value="1"` constraint vs. a true
  `<DTS:EventHandlers>` block) — both collapse to "code inside the catch block" on the right. Gate
  2 comparison logic must therefore treat "any node reachable only via a failure predicate or an
  event handler" as one right-side category (catch-block content) when matching against the two
  distinct left-side categories, and report a left/right shape difference explicitly rather than
  silently merging them into a false match.
- Packages with **no** failure-predicate edge and **no** event handler (`Package_Legacy`,
  `Package_Transforms`) have a `catch` block that only logs and reports failure — no
  `handlersRun` list, no inlined SQL — confirming the catch-block content is directly
  proportional to what the left side actually declares, not boilerplate.

### 4.6 Naming/provenance markers usable for reverse-mapping

- Package `DTS:ObjectName` → `internal sealed partial class <ObjectName>Package`, plus an
  XML-doc comment `/// <summary>Ported from SSIS package '<ObjectName>'.</summary>`.
- Every top-level executable's `DTS:ObjectName` → an exactly-matching C# method name
  (`SQL_TruncateTargets`, `DFT_LoadCustomers`, `SEQ_Prepare`, `FEL_SampleFiles`, ...), called via
  `await <Name>(uow, ct);` in its parent's body (`RunAsync` or the owning container's method).
  This is the primary, reliable node-identity link for anything **except** data-flow-internal
  components (§4.4) and failure/event-handler targets (§4.5), which have no such call site.
  Script Task classes carry an additional, explicit doc comment: `/// Ported SSIS Script Task
  '<ObjectName>' (original language: VisualBasic).` (`Package/ScriptTasks/*.cs`), reproducing the
  same naming discipline seen for Execute SQL Task classes in the Gate 1 notes.
  A Script Task's `RunAsync` follows the exact same `Step(...)`-mediated shape as every other step
  type — it is not a structurally special node in the call graph, only its class layout differs
  (a `partial` class whose actual body is hand/AI-authored elsewhere, per the Gate 1 notes' "seam
  type" designation).
- A concurrency group gets an explicit `// Concurrent (<N> executables -- SSIS ran these with no
  ordering constraint between them): - <Name> - <Name> - <Name>` comment immediately above its
  `Task.WhenAll(...)` (§4.3) — directly enumerates the node names in that group.
  A single-successor edge following a concurrency group gets a `// Flow <N>: <From> → <To>`
  comment (`Package_Exports.cs`) naming both endpoints of that specific edge.
  An expression-gated edge gets a `// Guard: <expression>` comment plus the literal expression
  string repeated inside the generated skip-log call (§4.2) — both reproduce the exact
  `DTS:Expression` attribute value from the left side.
  All of these comments are useful **evidence to cross-check a computed match**, but — per the
  Gate 1 notes' warning that a generated comment's claim about itself must never be trusted at
  face value — Gate 2 must still compute the left-side concurrency grouping and edge list
  independently from the XML and use these comments only as corroborating evidence, never as the
  source of truth.

## 5. Coverage table — which package exercised which shape

| Shape | Package | Package_Advanced | Package_Exports | Package_Legacy | Package_Transforms |
|---|---|---|---|---|---|
| Linear precedence-constraint chain (no branching) | | | | ✓ | ✓ |
| Conditional/expression-gated constraint (`EvalOp`/`Expression`) | ✓ | | | | |
| Failure-predicate constraint (`Value="1"`, inline, not an event handler) | ✓ | | | | |
| `<DTS:EventHandlers>` (`OnError`) block | | ✓ | | | |
| Sequence Container (`STOCK:SEQUENCE`) with ≥2 children + own constraint scope | | ✓ | | | |
| ForEach Loop Container (`STOCK:FOREACHLOOP`) | | ✓ | | | |
| Disabled node (`DTS:Disabled="True"`) with a real incoming edge | | | | ✓ | |
| Concurrency group (no constraint between ≥2 siblings), confirmed via absence-of-edge method | | ✓ (3 nodes) | ✓ (3 nodes) | | |
| Data-flow `<paths>`/`<path>` element, single output→single input | ✓ | ✓ | ✓ | ✓ | ✓ |
| Data-flow fan-out path (Conditional Split / Multicast, multiple outputs from one component) | | | | ✓ (Multicast) | ✓ (Conditional Split ×2) |
| Generated: flat sequential `await` call list matching left chain | ✓ | (partial — see concurrency) | (partial — see concurrency) | ✓ | ✓ |
| Generated: `Task.WhenAll` + per-branch `RunBranchAsync`/own transaction | | ✓ | ✓ | | |
| Generated: `if`/`else` reproducing an expression-gated edge, with skip-log evidence | ✓ | | | | |
| Generated: catch-block-inlined failure handler (`Value="1"` style) | ✓ | | | | |
| Generated: catch-block-inlined event handler (`<DTS:EventHandlers>` style) | | ✓ | | | |
| Generated: Sequence Container → nested method with flat child call list | | ✓ | | | |
| Generated: ForEach Loop Container → single call site, loop mechanics opaque | | ✓ | | | |
| Generated: Conditional Split/Multicast → branch array inside one DFT method (no per-component call site) | | | | ✓ (Multicast) | ✓ (ConditionalSplit ×2) |
| Generated: Merge Join → two local functions inside one DFT method | | | | | ✓ |
| Generated: `// Concurrent (...)` / `// Flow N: ...` / `// Guard: ...` provenance comments | ✓ (Guard) | ✓ (Concurrent) | ✓ (Concurrent, Flow N) | | |

No sample exercises: a completion-value (`Value` other than the observed `1`) constraint, an
`EvalOp` value other than `3`, `DTS:LogicalAnd="False"` (OR-combination of multiple incoming
edges), an `OnFailure`/other named event handler besides `OnError`, an event handler with 2+ tasks
(and therefore its own nested `<DTS:PrecedenceConstraints>` scope), a Sequence/ForEach Loop
container with its own concurrency group among its children, a `ForEach Loop` enumerator type
other than the file enumerator, or an `ExecutePackageTask` (parent/child package call). Per the
plan's §3, these remain unbuilt registry entries — not guessed — until a real sample or Microsoft
doc confirms their shape.

## 6. Open questions / risks for Gate 2 implementation

1. **Node granularity mismatch between the two sides is real, not hypothetical.** The left graph
   (per the plan's own wording) has one node per data-flow component; the right graph collapses
   every data-flow component inside one Data Flow Task into constructor arguments/local functions
   of a single method (§4.4). A naive 1:1 node-count comparison between "every SSIS executable +
   every pipeline component" and "every generated method" will always show a mismatch that isn't a
   real bug. Gate 2 needs an explicit two-tier comparison: control-flow-level nodes (1:1 with
   `await`-called methods) vs. data-flow-internal nodes (recovered from within one DFT method's
   body, compared against that DFT's own `<components>`/`<paths>`, never against the outer
   call graph).
2. **Concurrency-group detection must not rely on generated comments.** The `// Concurrent (...)`
   comments (§4.3, §4.6) happen to be accurate in both observed cases, but the Gate 1 notes already
   documented an instance of a generated doc comment being provably false about its own call
   graph. Gate 2 must compute the left-side concurrency grouping independently (absence-of-edge
   method, §1.3) and independently detect `Task.WhenAll`/similar constructs on the right, using the
   comments only as corroborating evidence in the report, never as the extraction source.
3. **Two structurally different left-side "runs on failure" mechanisms collapse to one right-side
   shape** (§4.5: inline `Value="1"` constraint vs. `<DTS:EventHandlers>` block both become
   catch-block content). A comparator that expects a distinct right-side node per left-side
   mechanism will misreport. This needs a deliberate category: "left node reachable only via a
   failure path" (subdivided by which of the two left mechanisms) compared against "catch-block
   content" as a single right-side unit, with sub-evidence (the `handlersRun` string, the inlined
   SQL) used for finer-grained matching within that unit.
4. **ForEach Loop content is invisible to a top-level call-graph walk** (§4.4) — the single child
   task inside `FEL_SampleFiles` never appears as its own `await` call; it's a lambda body passed
   to `ForEachLoopStep`. Any extraction that walks `RunAsync`/container-method bodies looking only
   for `await <Name>(...)` statements will silently miss ForEach Loop children entirely. This
   needs a dedicated extraction rule for the `ForEachLoopStep`/`*LoopAction` constructor-argument
   shape, not a generic call-site scan.
5. **Constraint scope-walking correctness depends on matching container boundaries on both
   sides.** The left side's `<DTS:PrecedenceConstraints>` scoping (§1.2) means a naive "collect
   every `PrecedenceConstraint` in the document into one global edge list" loses which container
   each edge belongs to — necessary for computing concurrency correctly per-container (a
   concurrency group must be computed within one container's children, not globally, once nested
   containers with their own groups exist — unobserved in these 5 samples but plausible for a real
   package). The extractor must preserve container membership from the start, not flatten early.
6. **The event-handler and inline-failure-edge left-side shapes were each observed only once
   across all 5 samples** (§1.5, §1.1/1.4) — a single data point each. Per the plan's §3 discipline,
   treat the specific C# shapes in §4.5 as "confirmed for this one instance," not yet "confirmed
   pattern across multiple real files" the way the Gate 1 notes require for genuinely generic
   registry entries — a second real sample exercising either mechanism should be read before
   treating the catch-block shape as a stable contract to build a strict comparator against.
7. **Multi-incoming-edge combination logic (`LogicalAnd`) is entirely unobserved.** Every sample
   constraint is the sole incoming edge on its target, so `DTS:LogicalAnd`'s True/False AND/OR
   semantics for combining ≥2 incoming constraints on one node has no generated-code counterpart
   to compare against yet. Must degrade to `Unverifiable`/`Unrecognized` if encountered on a real
   package rather than guessing how the generator would render an OR-combination.
8. **Data-flow path fan-out (Conditional Split/Multicast) was confirmed only as "branch array in
   one constructor call"** (§4.4) for exactly two component types. A component type with a
   fan-**in** shape on the right (e.g. how Merge/Merge Join's two-input convergence is represented
   beyond the two-local-functions pattern seen once in `Package_Transforms.cs`) has only that one
   example — the general "N-input component → generated shape" mapping is not yet proven across
   multiple real files.
