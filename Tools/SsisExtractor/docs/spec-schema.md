# spec.json contract

Status: **slice 5** (see `../../../Phase0-Extractor-Plan.md` §8 for the build-slice list).
This document describes what `ssisx extract` writes today; it will grow with each slice
rather than being rewritten, so a diff on this file is itself a changelog of the contract.
`ssisx report`/`ssisx diff` output is documented separately in `report-schema.md`.

## Input forms

`--input` accepts a `.dtsx`, a `.dtproj` (+ its packages), an **`.ispac`** (a deployed
project -- it is just a zip, so `ZipArchive` reads it; see the repo's CLAUDE.md trap 1), or a
directory containing `.dtproj`/`.ispac` files (`--recursive` to search subdirectories). All
four are handled by one shared `PackageLoader` in the CLI, so `extract`, `graph`, `report`,
and `diff` accept exactly the same shapes.

**An `.ispac` input writes no `project.spec.json`**: the archive carries a generated
`@Project.manifest`, not the authoring-time `.dtproj`, so there is no `ProjectSpec` to
serialize. Packages extract identically either way -- verified: both PoC packages read from
the built `.ispac` produce the same 100% coverage and the same structure as from source.

## Files written by `ssisx extract --out <dir>`

```
<dir>/
  _meta.json                     run metadata: tool version, run time, input, redaction flag, per-package coverage %
  project.spec.json              one per .dtproj processed (omitted for a loose .dtsx input)
  packages/<Package>.spec.json   one per package
```

## Files written by `ssisx graph --out <dir>` (slice 3)

```
<dir>/
  lineage/<Package>__<DataFlowTask path>.mmd   Mermaid flowchart, one per Data Flow Task
  lineage/<Package>__<DataFlowTask path>.dot   Graphviz DOT, same graph
```

A separate command from `extract` (plan §5.1's Mermaid/DOT output) -- it does not require
having run `extract` first, and `extract`'s own output tree above is unaffected by it. The
remainder of the plan's full tree -- `inventory.csv`, `findings.md`, the portfolio reports --
is slice 4.

## Format

- **JSON, UTF-8, no BOM, indented, trailing newline.**
- **Object keys are sorted alphabetically at every nesting level** (`Ssis.Extract.Model.Serialization.StableJsonWriter`).
  This is what makes `git diff` on a spec.json a real package-change detector instead of
  reordering noise. **Array element order is never changed** -- it reflects document order
  in the source XML (e.g. flat-file column order), which is itself deterministic for a
  given input file and is semantically meaningful, unlike JSON object key order.
- **Property names are exact C# property names (PascalCase)** -- the POCOs in
  `src/Ssis.Extract.Model` *are* the schema. There is no separate DTO/wire-format layer;
  reading the model source is reading the contract.
- **Nulls are explicit, not omitted.** A field is present with `null` when the source XML
  simply doesn't carry that attribute (a normal, expected state throughout the DTS/SSIS
  schemas -- most attributes are only serialized when they differ from their default), so
  every spec.json has the same shape regardless of what happened to be set. Don't read
  "field absent" as an error condition anywhere in this contract; there is no such thing.

## Redaction

`ConnectionString` on every connection manager always has `Password=`/`Pwd=` stripped,
regardless of flags. `UnredactedConnectionString` carries the untouched value, but **only
when `--no-redact` was passed** -- otherwise it's `null`. This shape (two distinct fields,
not one field whose meaning changes) is deliberate: a spec.json's structure never changes
between a redacted and unredacted run, only whether one field is populated.

## The control-flow tree (slice 2)

`PackageSpec.Executables` is the package root's direct children; each
`ExecutableSpec.Children` recurses the same way for a nested container (Sequence/ForEach/
For Loop -- neither present in this PoC, but the walker is generic, not hardcoded to two
task types). Every node also carries its own `PrecedenceConstraints` (ordering just its
direct children) and a derived `Dag`:

- `Dag.TopologicalOrder` -- one valid ordering (Kahn's algorithm).
- `Dag.ParallelLevels` -- children grouped by longest-path "wave". Two nodes in the same
  level are *provably* unrelated (no path between them either direction); nodes in
  *different* levels might also be unrelated, but this representation won't surface that
  pair -- a deliberate, documented trade of completeness for a result that's cheap to
  compute and never misleading (see the doc comment on `ControlFlowDagSpec` for why this
  is correct and an all-pairs incomparability list wouldn't be). Neither of this PoC's own
  packages branches (both are straight-line chains), so this is proven with a synthetic
  unit test (`tests/Ssis.Extract.Tests/DagAlgorithmTests.cs`), not a golden file.
- `Dag.HasCycle` -- SSIS's designer won't author one, but a hand-edited file could; reported rather than looping forever.

At most one of `ExecutableSpec.ExecuteSqlTask` / `.DataFlowTask` / `.UnmappedTask` /
`.ScriptTask` is populated, by `ExecutableType`. `.ForEachLoop` is the one exception that
can be populated *alongside* a real container (`Children`/`PrecedenceConstraints`/`Dag`) --
see its own row below for why:

| ExecutableType | Payload | Notes |
|---|---|---|
| `Microsoft.ExecuteSQLTask` | `ExecuteSqlTask` | Full SQL text (`SqlStatementSource`), connection resolved by name via the task's DTSID reference against the package's own connection managers. `ParameterBindings`/`ResultBindings` are best-effort -- neither PoC package's task uses them. |
| `Microsoft.Pipeline` | `DataFlowTask` | Full pipeline model (`Pipeline`) plus derived column lineage (`Lineage`) -- see the next section. |
| `Microsoft.ScriptTask` | `ScriptTask` | Language, project name, VSTA version, `ReadOnlyVariables`/`ReadWriteVariables`, plus every `ProjectItem` (the VSTA project's own source/support files, verbatim text) and every `BinaryItem`'s name (a precompiled cache, content deliberately not captured). No entry-point field: confirmed the saved `.dtsx` carries none for a VSTA-hosted Script Task (the only kind SSIS 17 creates) -- by convention it's always `ScriptMain` inside a `ScriptMain.cs`/`.vb` project item. See docs/report-schema.md's "Script Task source extraction" section for the full evidence (a real SSDT-authored Script Task, not just the synthetic fixture) and `ssisx report`'s `scripts/<Package>/<Task>/` output. |
| `STOCK:FOREACHLOOP` | `ForEachLoop` (in addition to being a real container) | Enumerator config + variable mappings -- these live in `<ForEachEnumerator>`/`<ForEachVariableMappings>`, siblings of `<Executables>`, not inside `<ObjectData>` (this executable type doesn't have one). Before this payload existed those two elements were invisible to both the model and the coverage percentage -- see report-schema.md for the fix. Bespoke parsing covers the File enumerator only; any other enumerator type gets `EnumeratorCreationName` plus a raw XML fallback, same honesty pattern as `UnmappedTask`. |
| anything else | `UnmappedTask` | Plan's mandatory "anything else" row -- raw `DTS:ObjectData` XML, so an unanticipated task type on a client's 50 packages is loud, not silently skipped. |

Event handlers (`EventHandlers` on the package and on any `ExecutableSpec`) are wired with
the same recursive shape but are **unverified** -- neither PoC package has one. Treat the
exact attribute names (particularly `EventHandlerSpec.EventName`'s source attribute) as
best-effort until confirmed against a real example.

## The Data Flow (pipeline) model and lineage (slice 3)

`ExecutableSpec.DataFlowTask.Pipeline` (`PipelineSpec`) is a full, generic parse of every
`<component>` in a `Microsoft.Pipeline` task: `Properties`, `Connections` (resolved to
connection manager names), `Inputs`/`Outputs` and their columns (including each column's
own `Properties`), and `Paths`. This generic layer is populated identically for *any*
`componentClassID`, recognized or not -- an unfamiliar/third-party component on a client
package still gets its properties/columns captured completely, just without the bespoke
semantic payload below. `PipelineInputSpec`/`PipelineOutputSpec` also carry their own
`Properties` (distinct from any individual column's) -- added after discovering Conditional
Split puts a whole case's `Expression`/`FriendlyExpression`/`EvaluationOrder` at the output
level, not on a column; before that field existed an output-level `<properties>` block was
invisible to the generic model entirely (see report-schema.md).

`PipelinePropertySpec.IsArray`/`.ArrayElements` cover a real, previously-lossy gap in that
same generic layer: an `isArray="true"` property (`<arrayElements><arrayElement>text
</arrayElement>...</arrayElements>`) used to collapse through `.Value` into one run-on
string with every element boundary silently lost -- found building Script Component
support (report-schema.md). `.Value` is `null` for an array property; read
`.ArrayElements` instead.

On top of the generic layer, `PipelineComponentSpec.FlatFileSource`/`.OleDbDestination`/
`.OleDbSource`/`.Lookup`/`.ConditionalSplit`/`.ScriptComponent` add component-specific
extraction (plan §4.7's per-component table) for the six types this project has real
evidence for -- two from the PoC's own packages, four from synthetic object-model-built
fixtures (report-schema.md; `Lookup`/`ConditionalSplit`/`ScriptComponent` still have no
client-package evidence). `FlatFileSource`/`OleDbDestination`/`OleDbSource` share the same
shape: `ConnectionName`, the source/target table or `SqlCommand`, and a derived
`ColumnMappings` list (component column ↔ external file/table column, built from
`externalMetadataColumnId` cross-references, not a separate section in the source XML).
`Lookup` adds `MatchOutputName`/`NoMatchOutputName` (SSIS's own fixed names for this
component, not user-renamed) and a best-effort `ReferenceColumns` list parsed out of the
component's own `ReferenceMetadataXml` custom property -- an internal, undocumented
format, not the standard `externalMetadataColumns` mechanism the other three use (confirmed
empty on this fixture even with a join key set). `ConditionalSplit` promotes each
non-default/non-error output's own properties into a `Cases` list, ordered by
`EvaluationOrder`. Derived Column needed no dedicated payload type: every output column
already carries promoted `Expression`/`FriendlyExpression` fields (pulled from its own
`Properties`) regardless of component type.

`ScriptComponent` is the pipeline-transform sibling of `ExecutableSpec.ScriptTask`, and the
one bespoke payload here NOT discriminated by `ComponentClassId` -- a Script Component's own
class ID is the generic `"Microsoft.ManagedComponentHost"` (confirmed real, not a guess: any
managed pipeline component could in principle use that same host); `PipelineReader` checks
the component's own `UserComponentTypeName` custom property (`"Microsoft.ScriptComponentHost"`)
instead. See docs/report-schema.md's "Script Component source extraction" section for the
full evidence, including the genuinely different comma-vs-semicolon `ReadOnlyVariables`/
`ReadWriteVariables` delimiter convention from `ScriptTaskPayload`'s, and what's confirmed
about `SourceCodeItems`' structure versus what isn't (no per-element file identity, unlike
Script Task's named `ProjectItems`).

`ExecutableSpec.DataFlowTask.Lineage` (`LineageSpec`, plan §5.1) is *derived*, not parsed --
built by `Ssis.Extract.Dtsx.LineageBuilder` from the `PipelineSpec` above, offline from any
XML. **Key discovery, evidenced from `LoadEmployees`'s own pipeline, not assumed from
documentation:** a `<path>` is a physical buffer connection, not what determines column
lineage. A synchronous transform's passthrough columns (e.g. Derived Column's own
`EmployeeID`/`Department` inputs, never re-emitted as its own output columns) are consumed
downstream by matching `lineageId` directly against the column's *true* original producer,
skipping the intermediate component's output entirely -- there is no `<path>` from the flat
file source straight to the OLE DB destination for those two columns, yet that is exactly
where their value comes from. So `LineageSpec.Edges` is built by matching `lineageId`
globally across every inputColumn/outputColumn in the pipeline, never by walking
`PipelineSpec.Paths`. This also happens to be exactly what the plan's own §5.1 example
diagram shows (direct arrows bypassing the transform's subgraph for passthrough columns) --
confirmation, not coincidence.

Each edge is `Kind = "PathFlow"` (unmodified passthrough into a consuming inputColumn) or
`"ExpressionDerived"` (a new output column computed from one or more upstream lineageIds
referenced in its `Expression` as `#{lineageId}`; the edge carries the whole expression
text, since the source XML doesn't segment which part came from which reference).
`ConstantColumns` lists output columns whose expression has zero `#{...}` references (e.g.
`GETUTCDATE()`) -- deliberately no incoming edge, not a fabricated one. `UnusedColumns`
lists output columns whose `lineageId` is never referenced anywhere downstream in the same
pipeline -- both PoC packages score zero here (every source column is either transformed or
passed straight through to the destination), so this is proven against synthetic pipelines
in `tests/Ssis.Extract.Tests/LineageBuilderTests.cs`, the same reasoning `DagAlgorithmTests`
already established for the control-flow DAG. Type/length-narrowing detection along a
lineage chain (also mentioned in plan §5.1) is **not yet implemented** -- noted as deferred
rather than silently absent.

`ssisx graph --input <path> --out <dir>` renders this into `<dir>/lineage/
<Package>__<DataFlowTask path>.mmd` (Mermaid) and `.dot` (Graphviz) pairs, one per Data Flow
Task, using the same node/edge set: one subgraph per component, a node per *produced*
(non-error output) column plus a node per input column of a "sink" component (no real
outputs of its own, e.g. an OLE DB Destination) so landed columns get their own terminal
nodes -- a plain passthrough input on a non-sink transform gets no separate node, matching
the plan's own example diagram rather than a literal one-node-per-XML-element dump. Constant
columns are highlighted. Does not require a prior `extract` run; it re-derives lineage
directly from the parsed pipeline, the same call `DtsxPackageReader.Read` itself makes.

## The "unmapped" bag -- honesty over false confidence

`PackageSpec.Unmapped` is a list of `{ Location, Reason, RawXml }`. As of slice 3 this only
ever contains `Package/DesignTimeProperties` (pure GUI layout, elided to a length note --
see below) and, if present, `Package/Configurations` (legacy Package-Deployment-Model
config, not yet modeled). Pipeline content is now fully modeled (see above), not captured
as raw XML at all -- there is nothing pipeline-related left in this bag for either PoC
package, which is also why both now score 100% coverage (see below).
`Package/DesignTimeProperties` is the one deliberate, *permanent* exception: it's pure GUI
layout (the file's own embedded XML comment says so), so only its byte length is kept --
otherwise every designer drag-and-drop would look like a package change in `git diff`, and
it's excluded from the coverage percentage's denominator entirely (see below), not just
deferred.

## Coverage percentage (plan §7.2)

`PackageSpec.Coverage` (`{ TotalElements, UnmappedElements, ExcludedElements, CoveragePercent }`)
and `_meta.json`'s `PackageCoverage` (the same number, one per package, for a caller who
doesn't want to open every spec.json). Counted in XML **elements**, not elements +
attributes as the plan's wording suggests -- a known simplification; a package could
score high while still dropping some attribute-level detail this metric can't see.
`ExcludedElements` (currently just `DesignTimeProperties`) is subtracted from both sides
of the percentage so a permanent non-goal doesn't cap the score below 100% forever.

`ssisx extract --fail-under <pct>` exits 3 (extraction output is still written) if any
package's `CoveragePercent` is below the threshold -- meant for CI gating once client
packages are in the picture. Both PoC packages scored ~19-20% through slice 2 (their
dominant XML content by element count was each package's Data Flow Task pipeline
internals, kept as raw XML in `DataFlowTaskMarker` back then) and now score **100%** as of
slice 3, exactly as predicted -- every element under `<pipeline>` in both packages is now
structurally modeled by `PipelineReader` (confirmed by enumerating every distinct element
name under `<pipeline>` in both files and checking each one is read somewhere), so there is
nothing left in `Unmapped` for either package. This number will drop again the moment a
client package uses a component type or task type this build slice hasn't modeled yet --
that's the metric doing its job, not a regression to chase down.

## The three type-code lookup tables -- do not conflate them

CLAUDE.md trap 12 (this repo, root) documents that SSIS XML uses more than one numeric
type-code space for what looks like the same concept. `Ssis.Extract.Model.Shared.SsisTypeCodeMaps`
keeps three separate lookups, each documented at its own call site with the evidence for
its mapping:

| Context | Where it appears | Table | Confidence |
|---|---|---|---|
| Project/package parameter `DataType` | `Project.params`, `.dtproj` manifest | `System.TypeCode` ordinals (18 = String) | Authoritative -- BCL contract, resolved via `Enum.IsDefined`, not hand-maintained |
| Variable/package-parameter declared `DataType` | `.dtsx` `DTS:VariableValue`/`DTS:PackageParameter` | OLE Automation VARIANT-derived (8 = String) | Only fixture-evidenced + unambiguous codes included; unknown renders `Unknown(n)` rather than guessing |
| Flat-file column / pipeline `DataType` | `.dtsx` `DTS:FlatFileColumn` | SSIS DT_* pipeline buffer types (130 = DT_WSTR) | 4 values cross-validated against this PoC's own textual `dataType` attributes; rest is the published DT_* table, lower confidence |

If a client package surfaces a code not in one of these tables, it renders as
`Unknown(n)`/`DT_UNKNOWN(n)` rather than a fabricated label -- extend the table only after
confirming the value against the runtime, the same "ask the runtime, don't guess" approach
CLAUDE.md trap 12 itself used to resolve this originally. `Ssis.Extract.ObjectModel` (plan
§7.3, built post-slice-5 -- see report-schema.md/README.md) is exactly this kind of
runtime-truth tool, though its current scope is structural counts, not type-code decoding;
extending it to print a live `Variable`/`Parameter`/pipeline column's actual `DataType`
value for an unknown code would be a natural, small addition when one is next hit.

## Known naming trap already hit once here

`PackageSpec` records the `.dtsx` file's own location as `SourceDtsxPath`, **not**
`FilePath`. An earlier version used `FilePath` and it collided with
`ConnectionManagerSpec.Parsed.FilePath` (a flat-file connection manager's actual resolved
CSV/file path -- real extracted content) under the golden-test normalizer, which matches
JSON keys by name: it silently blanked real content on the assumption it was the
environment-dependent package path. Keep every "this spec object's own source file
location" field named `Source...Path` (matching `SourceDtprojPath`/`SourceProjectParamsPath`)
and never reuse that shape for a field holding actual extracted data.
