# `.dtsx` XML and generated-C# empirical notes — SQL-bearing shapes (Gate 1)

Compiled from direct reading of the 5 canonical `.dtsx` files under
`D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\` (`Package.dtsx`,
`Package_Advanced.dtsx`, `Package_Exports.dtsx`, `Package_Legacy.dtsx`,
`Package_Transforms.dtsx`) and the generated C# under
`D:\tmp\Sandeep_Packages\gen\generate\<PackageName>\`. No file under `extract/`, `gen/gaps*`,
`svk/`, `coverage-report.md`, `generate-report.md`, or any `bin/`/`obj/` folder was read — see
the independence rule in [SSIS-Rewrite-Validation-Plan.md](../SSIS-Rewrite-Validation-Plan.md).

This is empirical evidence, not a spec. Sample-specific values (table/column names, connection
strings) appear only as illustrative snippets — extraction logic must key off the structural
shapes described here, never off these specific names.

## 1. Left side — where SQL text lives in `.dtsx` XML

### 1.1 `Microsoft.ExecuteSQLTask` — attribute value on `SQLTask:SqlTaskData`

SQL text is a plain **XML attribute value**, never CDATA, never element text:

```xml
<DTS:Executable DTS:CreationName="Microsoft.ExecuteSQLTask" DTS:ObjectName="SQL_TruncateTargets" ...>
  <DTS:ObjectData>
    <SQLTask:SqlTaskData
      SQLTask:Connection="{034991FB-0BC3-4377-BF3D-2E2274CC650A}"
      SQLTask:SqlStatementSource="TRUNCATE TABLE dbo.StagingCustomers;&#xA;TRUNCATE TABLE dbo.SalesSummary;"
      xmlns:SQLTask="www.microsoft.com/sqlserver/dts/tasks/sqltask" />
  </DTS:ObjectData>
</DTS:Executable>
```

- Multi-line SQL uses literal `&#xA;` XML numeric character references, not CDATA. CDATA is
  **never** used for SQL anywhere in these 5 samples — extraction must not assume it will be.
- `SQLTask:Connection` references a connection manager by **`DTS:DTSID` GUID** (braces included),
  not by `refId`/name — resolve via `DTS:ConnectionManagers/DTS:ConnectionManager/@DTS:DTSID`.
- No `SQLTask:ResultSetBinding` or `SQLTask:ParameterBinding` child elements were observed in any
  of the 5 samples — result-set capture and SQL-parameter binding for this task type remain
  unobserved shapes (do not pre-build extraction for them; report `Unrecognized` if encountered).
- **`DTS:PropertyExpression` override**: one instance (`FEL_SampleFiles\SQL_LogFileName` inside a
  ForEach Loop, in `Package_Advanced.dtsx`) carries *both* a static `SqlStatementSource` attribute
  (a stale/placeholder value) *and* a sibling `<DTS:PropertyExpression DTS:Name="SqlStatementSource">`
  element holding a live SSIS expression that concatenates a loop variable. The generated C#
  confirmed the **expression** is what actually executes (its `BuildStatement` method takes a
  runtime parameter and reproduces the expression's concatenation), not the static attribute.
  Extraction must check for a `PropertyExpression` targeting the same property name before
  trusting the static attribute value as ground truth.
- **`DTS:Disabled="True"` executables produce zero generated C#** — confirmed on one instance
  (`Package_Legacy.dtsx`'s `SQL_LegacyStep_DISABLED`): no matching class, no call site, no
  `RunAsync` reference anywhere in the generated project, even though it has real SQL text and a
  live precedence-constraint edge to it. **The left-side inventory (Gate 1 and Gate 2) must
  exclude `Disabled="True"` executables**, or every one will surface as a false `LeftOnly`/
  `Unmatched` finding. The precedence-constraint edge into a disabled node also needs an explicit
  Gate 2 rule (an edge into a node that never executes).
- Some Execute SQL Tasks are reached only via error/failure paths (`OnFailure` precedence
  constraint, or a package-level `OnError` event handler) or live inside containers (ForEach Loop,
  Sequence) rather than top-level — the executable hierarchy must be walked recursively, not
  assumed flat.
- A task can target a **non-primary connection manager** (`Package_Legacy.dtsx`'s
  `SQL_CacheSet_SecondDb` uses a distinct connection manager from the package's main one). The
  generated C# gave this a different step type (`SecondaryConnectionSqlStep` vs. plain
  `ExecuteSqlStep`) that re-resolves a named connection string from configuration — connection
  manager identity, not just SQL text, is a real parity signal here.

### 1.2 Pipeline (data-flow) components — SQL as element text inside a named `<property>`

Unlike Execute SQL Task, pipeline-component SQL lives as **element text content** of a
`<property name="SqlCommand">` node, with real (unescaped) newlines since it's element content:

```xml
<property dataType="System.String" description="The SQL command to be executed."
    name="SqlCommand" UITypeEditor="Microsoft.DataTransformationServices.Controls.ModalMultilineStringEditor">SELECT
    o.CustomerID,
    c.FullName
FROM dbo.Orders o
INNER JOIN ...</property>
```

Observed component shapes carrying (or not carrying) this property:

- **`Microsoft.OLEDBSource`** (componentClassID given directly): `AccessMode` (numeric,
  `typeConverter="AccessMode"`) discriminates the mode — `2` = "SQL Command" (SQL text populates
  `SqlCommand`, `OpenRowset`/`OpenRowsetVariable`/`ParameterMapping` present but empty).
  No `OpenRowset`-table-mode instance was observed for a *source* in these samples (only for
  destinations — see below), so that source-side mode remains unobserved.
- **`Microsoft.OLEDBDestination`** (componentClassID given directly): `AccessMode=3` ("table/view,
  fast load") is the only mode observed. `SqlCommand` is **always present but empty**; the table
  identity is instead the plain-text `OpenRowset` property, formatted `[schema].[table]`
  (brackets). `FastLoadOptions` varies per-instance (sometimes populated e.g.
  `TABLOCK,CHECK_CONSTRAINTS`, sometimes an empty string) — a real, expected variation, not a bug.
- **`Microsoft.ManagedComponentHost` wrapping a real type** — `componentClassID` alone is **not
  always a reliable type discriminator**. Both an ADO.NET source and an ADO.NET destination
  observed in `Package_Exports.dtsx` report `componentClassID="Microsoft.ManagedComponentHost"`;
  the real type only appears in a nested `<property name="UserComponentTypeName">Microsoft.Xxx</property>`
  value (`Microsoft.DataReaderSourceAdapter` for ADO.NET Source, `Microsoft.ADONETDestination` for
  ADO.NET Destination). Extraction must check `UserComponentTypeName` when `componentClassID` is
  the generic host.
  - ADO.NET **source** (`AccessMode=1`, `typeConverter=".../DataReaderSourceAdapter+EnumToStringConverter"`):
    same SQL-command-in-element-text shape as OLEDBSource; `TableOrViewName` present but empty.
  - ADO.NET **destination**: no `SqlCommand` property exists on this component type at all. Its
    only "SQL-adjacent" property is `TableOrViewName`, a plain string quoted with **double quotes**
    (`"dbo"."CustomerExportLog"`) — a different quoting convention from OLEDBDestination's
    bracket-quoted `OpenRowset` (`[dbo].[CustomerFixedImport]`). Both are literal
    schema-qualified table names; extraction must normalize both forms.
- **`Microsoft.OLEDBCommand`** — parameterized per-row SQL using a positional `?` marker:
  `EXEC dbo.usp_SetCustomerFlag ?, N'flagged'`. The parameter is bound via a synthetic,
  **`@`-prefixed** input/external-metadata column name (e.g. `@CustomerID`) distinct from real
  table columns — this `@` prefix is the marker that a column entry is a command parameter, not a
  real row column. Only one parameter observed in the samples; multi-parameter shape unobserved.
- **`Microsoft.Lookup`** — reference query in the same element-text `SqlCommand` shape as
  OLEDBSource, plus a sibling empty `SqlCommandParam` (unused in all samples — no `?` markers
  appeared inside a Lookup's `SqlCommand`). Only one Lookup exists across all 5 packages
  (`Package_Transforms.dtsx`, component named `LKP_Country`). Notable specifics:
  - `ConnectionType="0"`, `CacheType="0"` — full-cache lookup via the package's normal OLE DB
    connection manager; **no separate Cache Connection Manager** or `CACHE_`-prefixed component
    exists in this sample (parameterized/partial-cache Lookup shapes are unobserved).
  - The reference schema is declared **twice independently**: once as a `ReferenceMetadataXml`
    property (an XML-escaped nested-XML blob embedded as element text) and again structurally as
    normal `<outputColumns>` under the "Lookup Match Output". Both must be extracted and checked
    for internal agreement.
  - The **join key** (which input column joins to which reference column) is expressed via
    `<inputColumn>/<properties>/JoinToReferenceColumn>`, entirely separate from the `SqlCommand`
    text — the SQL text alone never reveals the join predicate.
- **`Microsoft.ExcelSource`** — no SQL at all in the one instance observed (`AccessMode=0`,
  `OpenRowset="DripEligibility$"` — a worksheet name, no brackets, trailing `$`). `SqlCommand`
  present but empty. A `SqlCommand`-mode Excel source (WHERE clause / column list) is unobserved.
- **`Microsoft.FlatFileSource` / `Microsoft.FlatFileDestination`**: no SQL-bearing property exists
  on these component types whatsoever (confirmed by direct inspection, not absence-by-omission) —
  the generated C# correspondingly has zero SQL text for these; "no SQL" degrades cleanly to "no
  SQL-bearing generated artifact expected," not a placeholder or empty string to match against.
- **`Microsoft.Multicast`**: no `<properties>` element at all (not even an empty one).

Declared schema is always **co-located inside the same `<component>`** as the SQL-bearing
property, under `<outputs>`/`<inputs>` with two parallel column lists: the pipeline-buffer view
(`outputColumns`/`inputColumns`, carrying `cachedDataType`/`cachedLength`) and the
source/destination-metadata view (`externalMetadataColumns`, carrying `dataType`/`length`). These
two views **can disagree** (a `length` mismatch between `outputColumns`/`inputColumns` and
`externalMetadataColumns` was observed for at least one column) — this is itself a candidate
Gate 3 finding shape, not just a Gate 1 concern.

### 1.3 Script Task / Script Component — SQL embedded in a binary/base64 blob, not visible as XML text

Both `Microsoft.ScriptTask` and the script-component variant of `Microsoft.ManagedComponentHost`
(`UserComponentTypeName=Microsoft.ScriptComponentHost`) store their actual source two ways in the
samples: as a `SourceCode`/`ScriptProject` array of named text items (visible, e.g. a
`ScriptComponent` subclass's `.cs` file appears as plain CDATA-free array-element text — readable
directly), and separately as a `BinaryCode`/`BinaryItem` base64-encoded compiled blob (opaque).
For at least one Script Task, SQL text used inside the script (confirmed indirectly via the
generated `Fills/*.Fills.cs` file, which contains real `SELECT`/`INSERT` statements with a
comment noting what the "original" script did) **could not be located as plain text anywhere in
the outer `.dtsx` XML** — only inside the compiled/zipped binary blob. This breaks the assumption
that SQL text always sits in a named, human-readable property: **Script Task/Component SQL
requires its own extractor** (decode + inspect the embedded project), and until that exists, any
SQL known only to live inside a script must be reported `Unverifiable`, never silently skipped or
silently assumed to match. This matches the plan's designation of Script Task/Component as
"seam" types that are never auto-translated and require human/AI-authored fill-in code.

### 1.4 Component type identity — two different discriminator mechanisms

- Package-level `<DTS:Executable>` nodes (tasks, containers, the package root, the Data Flow Task
  container) use `DTS:CreationName` (e.g. `Microsoft.ExecuteSQLTask`, `Microsoft.Pipeline`,
  `Microsoft.Package`).
- Pipeline `<component>` nodes (inside a `Microsoft.Pipeline`'s `ObjectData`) use
  `componentClassID` (e.g. `Microsoft.OLEDBSource`) — except when wrapped in
  `Microsoft.ManagedComponentHost`, where the real type is in `UserComponentTypeName` (§1.2).
- No sample used a bare friendly name (e.g. `"OLE DB Source"`) as the operative type key — friendly
  names only appeared as free text in `description`/`contactInfo` attributes. The friendly-name
  vs. `CreationName` duality flagged in the plan (§10, from the client portfolio scan) was not
  reproduced in these 5 samples; extraction must still handle it defensively since real
  unrevalidated client packages are documented to carry it.

## 2. Right side — where SQL text lives in generated C#

| Left shape | Generated shape | Where |
|---|---|---|
| `Microsoft.ExecuteSQLTask` (static SQL) | `public static class <ObjectName>Statement { public static string BuildStatement() => "..."; }` | `Mapping/<ObjectName>Statement.cs` |
| `Microsoft.ExecuteSQLTask` (expression-driven, e.g. ForEach-loop variant) | Same shape, but `BuildStatement(string <param>)` takes a parameter and reproduces the SSIS expression's string concatenation | `Mapping/<ObjectName>Statement.cs` |
| `Microsoft.OLEDBSource` / ADO.NET source (`AccessMode` = SQL command) | Inline `CommandText = "..."` string literal inside a `new SqlRowSource<T>("<ComponentName>", new SqlSourceOptions { ConnectionString = ..., CommandText = "..." }, <Reader>.Read, uow)` construction, in a method named after the component | `<PackageName>.cs` |
| `Microsoft.OLEDBCommand` (parameterized) | Composite-format string with SSIS `?` → .NET `{0}` placeholder(s), wrapped in `new OleDbCommandStep<T>("<PipelineName>", source, "EXEC ... {0} ...", row => new object?[] { row.<Column> }, ...)` | `<PackageName>.cs` |
| `Microsoft.OLEDBDestination` / ADO.NET destination (table mode) | **No SQL string at all.** Table identity becomes `entity.ToTable("<Table>", "<Schema>")` (brackets/quotes stripped, schema and name split into separate arguments) inside `modelBuilder.Entity<T>(...)`, paired with a plain POCO | `Model/<PackageName>DbContext.cs` (fluent config) + `Model/<Table>.cs` (POCO) |
| `Microsoft.Lookup` reference query | `command.CommandText = "..."` string assignment inside a generated cache-loader class's `LoadAsync` method | `Mapping/<ComponentName>Cache.cs` — **generated class name is not guaranteed to equal the SSIS component name** (observed: dtsx component `LKP_Country` → generated class `LKP_CountryCache`, generator appended "Cache" itself) |
| Script Task / Script Component logic (may include SQL) | Hand/AI-authored fill-in code, SQL as ordinary EF/ADO calls; **not always traceable to any single dtsx XML text location** (source may live only in the embedded binary blob, §1.3) | `Fills/<ObjectName>.Fills.cs` (also mirrored under `ScriptTasks/<ObjectName>.cs` for the task shell) |

Naming links confirmed between left and right:

- Execute SQL Task `DTS:ObjectName` → C# class `<ObjectName>Statement`, exact match. The generated
  class also carries an XML-doc comment of the form `/// Ported SSIS Execute SQL Task
  '<ObjectName>'.` — a reliable, generic provenance marker worth using for reverse-mapping.
- Pipeline component `name` → the C# method name emitting a `SqlRowSource`/`OleDbCommandStep`, and
  also the first string-literal constructor argument passed to it — a double, independently
  checkable naming link.
- The pipeline (Data Flow Task) `DTS:ObjectName` becomes the outer orchestration method that calls
  each component's method — i.e. two different names are in play per data flow (the task name and
  each component name), and extraction must track both.
- Destination table name → `ToTable(table, schema)` args plus a POCO class named after the **bare
  table name** (not the destination component's SSIS name) in `Model/<Table>.cs`.
- The row-shape class backing a `SqlRowSource<T>` is not uniformly named after one single thing:
  observed examples name it after the pipeline/task (`SalesSummarySqlRow`), and in another package
  after the **destination table** instead (`CustomerExportLogSqlRow`, for a source component named
  `ADO_SRC_Customers` feeding a `CustomerExportLog` destination) — the naming source is generator
  discretion, not a fixed rule; must be resolved by structural correlation (which columns/types
  match), not assumed from name alone.
- Folder names in the generated project are **not reliable content indicators** — a `Sql/` folder
  was found containing plain POCO row classes with zero SQL text in one package. Content must be
  inspected structurally, never inferred from folder/file naming.
- Doc comments and code claims in generated files (e.g. a comment claiming a class is "NOT
  referenced by Program.cs" when it demonstrably is called elsewhere) must never be trusted at
  face value — this is exactly the kind of "tool's own account of itself" the independence rule
  (plan §1) says to distrust; always verify against actual code structure/call sites.

## 3. Coverage matrix — which package exercised which shape

| Shape | Package | Package_Advanced | Package_Exports | Package_Legacy | Package_Transforms |
|---|---|---|---|---|---|
| ExecuteSQLTask (static) | ✓ | ✓ (2 of 3) | ✓ | ✓ | ✓ |
| ExecuteSQLTask (PropertyExpression override) | | ✓ | | | |
| ExecuteSQLTask (`Disabled="True"`) | | | | ✓ | |
| ExecuteSQLTask (non-primary connection manager) | | | | ✓ | |
| OLEDBSource (SQL command mode) | ✓ | ✓ | ✓ | | |
| OLEDBDestination (table mode) | ✓ | ✓ | | ✓ | ✓ |
| ADO.NET source (via ManagedComponentHost) | | | ✓ | | |
| ADO.NET destination (via ManagedComponentHost) | | | ✓ | | |
| OLEDBCommand (parameterized) | | ✓ | | | |
| Lookup | | | | | ✓ |
| ExcelSource | | ✓ | | | |
| FlatFileSource / FlatFileDestination (no SQL) | | | ✓ | ✓ | ✓ |
| Multicast (no SQL) | | | | ✓ | |
| Script Task/Component (SQL in binary blob) | ✓ | ? (not fully inspected) | | | |

No sample exercises: `Microsoft.DataConvert`, `Microsoft.ConditionalSplit` (SQL-relevant
aspects), a partial-cache/parameterized `Lookup`, a multi-parameter `OLEDBCommand`, an
`ExecutePackageTask`, or a `Sequence`/`ForEach Loop` container's own properties (only their
*contents* were inspected). Per the plan's §3, these remain unbuilt registry entries — not
guessed — until a real sample or Microsoft doc confirms their shape.

## 4. Open questions / risks for Gate 1 implementation

1. Script Task/Component SQL is not extractable from the outer `.dtsx` XML with `ElementTree`
   alone — needs a dedicated decode step (unzip the compiled VSTA project blob) or must be
   reported `Unverifiable` for now, per the plan's explicit-degradation rule (§3).
2. `Disabled="True"` executables must be filtered out of the left-side inventory before Gate 1/2
   comparison, with a separate, explicit accounting (e.g. a `DisabledOnLeft` category) rather than
   silently dropped or reported as a false mismatch.
3. `DTS:PropertyExpression` can override a static SQL-bearing attribute at runtime — the static
   value alone is not always ground truth; both must be captured, and the expression preferred
   when present.
4. Table-identity comparisons (OLEDBDestination/ADONETDestination) are structural (name + schema
   token comparison against `ToTable(...)` args), not SQL-text comparisons — `sqlglot` applies to
   the SQL-command shapes (ExecuteSQLTask, OLEDBSource/ADO.NET source SQL-command mode, Lookup,
   OLEDBCommand), not to table-mode destinations, which have no SQL text on either side.
5. Generated-code naming is a strong but not 100%-uniform correlation signal (see row-shape naming
   above) — the comparator needs a structural fallback (matching by columns/types) when name
   correlation is ambiguous, and must report `Unmatched` rather than guess when it can't decide.
