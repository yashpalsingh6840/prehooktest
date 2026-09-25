namespace Ssis.Extract.Codegen;

/// <summary>How one flow reads its rows -- a Flat File Source (CSV, via a FileSourceOptions
/// key) or an OLE DB Source (SQL, via a resolved connection string + SELECT text). Exactly one
/// concrete case per flow; ProgramEmitter switches on it to emit the matching IRowSource
/// registration.</summary>
public abstract record FlowSourceSpec(string ComponentName);
public sealed record CsvFlowSource(string ComponentName, string FileSourceKey, int SkipRows = 0) : FlowSourceSpec(ComponentName);

/// <summary>A Flat File Source whose connection manager has no column-NAME header at all --
/// SSIS's own "FixedWidth"/"RaggedRight" formats, read purely by character position (see
/// <c>FlatFileRuntimeShape.IsFixedWidthWithoutHeader</c> for exactly which connection managers
/// take this path instead of <see cref="CsvFlowSource"/>). <see cref="Columns"/> is the
/// file's own physical layout, in the connection manager's own column order.</summary>
public sealed record FixedWidthFlowSource(
    string ComponentName, string FileSourceKey, IReadOnlyList<FixedWidthColumnPlan> Columns, int SkipRows) : FlowSourceSpec(ComponentName);
/// <summary><see cref="SecondaryConnectionManagerName"/>, when set (Phase 5, added 2026-09-17),
/// means this source's own connection manager resolves to a DIFFERENT server/database than the
/// flow's own destination -- <c>Etl.Core.Data.SqlRowSource{TRow}</c> already opens its own
/// independent connection regardless of database (see that type's own doc comment), so the only
/// thing that actually changes is where the connection STRING comes from: not the package's
/// primary <c>Db()</c>, but a <c>SecondaryConnections:{name}</c> config entry -- the exact same
/// mechanism <see cref="ProgramSecondaryConnectionSqlStep"/> already established for the
/// write-side case (an Execute SQL Task against a second database). Null (the default) preserves
/// every pre-existing call site's behaviour byte-for-byte. A secondary-connection source is never
/// bound to the package's own transaction (no <c>uow</c> argument at its construction site) --
/// skipping <c>sp_bindsession</c> and falling back to READ UNCOMMITTED, the same already-accepted
/// tradeoff every other unbound <c>SqlRowSource</c> read in this codebase uses (a Lookup preload,
/// for instance) -- there is no meaningful "this package's active transaction" to bind to on a
/// different database anyway.</summary>
public sealed record SqlFlowSource(string ComponentName, string CommandText, string? SecondaryConnectionManagerName = null) : FlowSourceSpec(ComponentName);

/// <summary>An Excel Source, added 2026-08-28 testing this tool against a real third-party
/// portfolio (SSIS_From_Sandeep). Uses the same <see cref="FileSourceOptions"/>/FileSourceKey
/// mechanism a <see cref="CsvFlowSource"/> does for its file path (a workbook is just another
/// named folder/filename pair), plus the worksheet name and whether to skip its own header row.
/// <see cref="WhereFilter"/> (gap-audit Phase 3.4, 2026-09-02), when set, is a C# boolean
/// expression referencing <c>row.{Column}</c> (see <see cref="ExcelWhereClauseTranslator"/>) --
/// the underlying <c>ExcelRowSource{TRow}</c> is wrapped in a <c>FilteringRowSource{TRow}</c>
/// rather than given any query capability of its own, since it never had one.</summary>
public sealed record ExcelFlowSource(string ComponentName, string FileSourceKey, string WorksheetName, bool HasHeaderRow, string? WhereFilter = null) : FlowSourceSpec(ComponentName);

/// <summary>An XML Source (<c>Microsoft.XmlSourceAdapter</c>), added for Phase 5 of the
/// unsupported-component-types plan. Uses the same <see cref="FileSourceOptions"/>/FileSourceKey
/// mechanism a <see cref="CsvFlowSource"/>/<see cref="ExcelFlowSource"/> does for its file path,
/// plus the repeating row element's own local name (<see cref="RowElementName"/>) -- see
/// <c>Etl.Core.Xml.XmlRowSource</c>'s own doc comment for exactly how that's matched.</summary>
public sealed record XmlFlowSource(string ComponentName, string FileSourceKey, string RowElementName) : FlowSourceSpec(ComponentName);

/// <summary>A Merge Join: two independent underlying sources (each itself a
/// <see cref="CsvFlowSource"/>/<see cref="SqlFlowSource"/>, resolved exactly like a
/// single-source flow's own), combined by a generated <c>Etl.Core.Data.MergeJoinRowSource</c>
/// wrapping the mapper class <see cref="MergeJoinEmitter"/> emitted (<see cref="MapperClassName"/>,
/// providing <c>LeftKey</c>/<c>RightKey</c>/<c>Map</c>). <see cref="ComponentName"/> is the
/// Merge Join's own Name, used only for the wrapped IRowSource's own logging identity.</summary>
public sealed record MergeJoinFlowSource(
    string ComponentName,
    FlowSourceSpec LeftSource,
    FlowSourceSpec RightSource,
    string LeftRowTypeName,
    string RightRowTypeName,
    string MapperClassName,
    string KeyClrType,
    string JoinType) : FlowSourceSpec(ComponentName);

/// <summary>A genuinely multi-independent-source <c>Microsoft.Merge</c>/<c>Microsoft.UnionAll</c>
/// -- gap-audit Phase 3.6 (2026-09-02). Every side is itself a <see cref="FlowSourceSpec"/>
/// (scoped to <see cref="SqlFlowSource"/> only this round -- see <c>UnionPlan</c>'s own doc
/// comment), each built via its own local function, same shape
/// <see cref="MergeJoinFlowSource"/>'s own two sides already use. All sides share ONE row type
/// (<see cref="RowTypeName"/>) and ONE reader (<c>UnionEmitter.EmitReader</c>'s own output) --
/// real SSIS enforces matching schemas across every Merge/UnionAll input, so there is no
/// per-side row type the way a join's own Left/Right sides need. <see cref="IsSortedInterleave"/>
/// picks the runtime type: <c>MergeInterleaveRowSource&lt;TRow,TKey&gt;</c> (Merge, exactly 2
/// sides, dtexec-confirmed true interleave) or <c>ConcatenatingRowSource&lt;TRow&gt;</c>
/// (UnionAll, any number of sides, plain concatenation -- not independently dtexec-confirmed,
/// see that type's own doc comment). <see cref="KeyPropertyName"/>/<see cref="KeyClrType"/> are
/// populated only for the Merge case.</summary>
public sealed record UnionFlowSource(
    string ComponentName,
    bool IsSortedInterleave,
    IReadOnlyList<FlowSourceSpec> Sides,
    string RowTypeName,
    string? KeyPropertyName = null,
    string? KeyClrType = null) : FlowSourceSpec(ComponentName);

/// <summary>One aggregated column of an Aggregate flow -- <see cref="OutputPropertyName"/> on the
/// destination-facing row, computed from <see cref="SourcePropertyName"/> on the flow's own raw
/// SOURCE row per <see cref="AggregationTypeRaw"/>'s own measured semantics (see
/// <c>Ssis.Extract.Model.Pipeline.AggregatePayload</c>'s own doc comment for the full raw-value
/// mapping -- 1=Count, 2=CountAll, 3=CountDistinct, 4=Sum, 5=Average, 6=Minimum, 7=Maximum).
/// <see cref="SourcePropertyName"/> is null exactly for a column-less CountAll (2). Built
/// speculatively 2026-08-30 (Count only), widened 2026-09-02 (gap-audit Phase 3.3).</summary>
public sealed record AggregateFunctionFieldSpec(string OutputPropertyName, string? SourcePropertyName, int AggregationTypeRaw);

/// <summary>When an Aggregate's own GroupBy value is a Lookup-copied reference column (RBC_Demo_
/// ETL's own DFT_LookupAndAggregate: GroupBy Region, copied from LKP_Country's own reference
/// table, not a plain buffer column) rather than a plain source-row property -- built 2026-09-02
/// alongside the Lookup+Aggregate composed-flow round. <see cref="LookupVariableName"/> is the
/// same top-level cache variable <c>LookupPreload</c> already loads before any DI registration;
/// <see cref="JoinInputPropertyName"/> is the raw source row's own join-key property; the key
/// selector becomes <c>row => {LookupVariableName}[row.{JoinInputPropertyName}].{ReferenceColumnName}</c>
/// instead of the plain <c>row => row.{GroupByPropertyName}</c> every other Aggregate flow uses.
/// Every row reaching this point is already guaranteed a cache hit (see
/// <c>Etl.Core.Data.FilteringRowSource{TRow}</c>'s own doc comment), so the indexer never
/// throws.</summary>
public sealed record AggregateLookupKeySpec(string LookupVariableName, string JoinInputPropertyName, string ReferenceColumnName);

/// <summary>One GroupBy column's own destination-facing property name and CLR type -- a list on
/// <see cref="AggregateFlowSource"/> (not a single value), closing a real 3-column GroupBy found
/// in a genuine client portfolio. Exactly one entry reproduces the original, single-column shape
/// byte-for-byte (a plain <c>TKey</c>, a plain <c>row => row.X</c> key selector); more than one
/// uses a named C# tuple as <c>TKey</c> instead -- see <c>PackageClassEmitter</c>'s own Aggregate
/// case for exactly where that split happens.</summary>
public sealed record AggregateGroupByFieldSpec(string OutputPropertyName, string ClrType);

/// <summary>A Microsoft.Aggregate flow (GroupBy + Count), built speculatively 2026-08-30 -- see
/// <c>Ssis.Extract.Model.Pipeline.AggregatePayload</c>'s own doc comment for why (the one real
/// evidenced instance is unreachable, blocked by its own upstream Lookup regardless). Wraps
/// <see cref="InnerSource"/> (the flow's own raw source, e.g. a <see cref="SqlFlowSource"/>) in
/// <c>Etl.Core.Data.AggregateRowSource&lt;TSourceRow,TKey,TRow&gt;</c> -- <see cref="SourceRowTypeName"/>
/// is that raw source's own row type (TSourceRow); the outer <see cref="FlowSourceSpec.ComponentName"/>
/// carries the Aggregate component's own name, used only for the wrapped IRowSource's own
/// logging identity, matching <see cref="MergeJoinFlowSource"/>'s own convention.
///
/// <see cref="LookupKey"/>, when set (2026-09-02, the real evidenced Lookup+Aggregate composed
/// shape), swaps the key selector to read through the Lookup cache instead of a plain row
/// property -- see <see cref="AggregateLookupKeySpec"/>'s own doc comment. <see cref="LookupFilter"/>
/// is a genuinely SEPARATE, independent axis: whenever the flow's own Lookup redirects a miss
/// away (rather than failing the component), every row must be excluded from the aggregate
/// entirely, regardless of whether the GroupBy value itself happens to come from the lookup
/// cache or is a plain passthrough column -- so <see cref="LookupFilter"/> wraps
/// <see cref="InnerSource"/> in a <c>FilteringRowSource&lt;TSourceRow&gt;</c> whenever a miss is
/// possible, and <see cref="LookupKey"/> only ever affects how the key ITSELF is read. The two
/// are set independently by the caller, though the one real evidenced flow sets both.</summary>
public sealed record AggregateFlowSource(
    string ComponentName,
    FlowSourceSpec InnerSource,
    string SourceRowTypeName,
    List<AggregateGroupByFieldSpec> GroupByColumns,
    List<AggregateFunctionFieldSpec> Functions,
    AggregateLookupKeySpec? LookupKey = null,
    (string LookupVariableName, string JoinInputPropertyName)? LookupFilter = null) : FlowSourceSpec(ComponentName);

/// <summary>The "no-match is the live route" Lookup shape (Phase 3, gap-audit plan
/// concurrent-whistling-turing.md, 2026-09-16) -- the classic "insert-if-new" dimension load
/// pattern: NoMatchBehavior=1, the Match output left unrouted (or itself a discarded RowCount
/// dead end), and the No-Match output wired straight to a destination. Every row reaching the
/// destination is, by definition, one the Lookup did NOT find a reference row for, so
/// <see cref="InnerSource"/> is wrapped in a <c>FilteringRowSource&lt;TRow&gt;</c> keeping only
/// rows whose join key is ABSENT from the cache -- the mirror image of
/// <see cref="AggregateFlowSource.LookupFilter"/>'s own <c>ContainsKey</c> check (which keeps
/// matches). No reference columns are ever copied for this shape (a miss has no reference row to
/// copy from), so the owning flow's own transform needs no cache parameter at all --
/// <c>ProgramFlowSpec.TransformNeedsLookupCache</c> is false whenever this type is used, even
/// though <c>ProgramFlowSpec.Lookup</c> (the preload) is still populated, since the cache is
/// still needed here, in the filter, just not in the transform.</summary>
public sealed record LookupNoMatchFilteredFlowSource(
    string ComponentName, FlowSourceSpec InnerSource, string LookupVariableName, string JoinInputPropertyName) : FlowSourceSpec(ComponentName);

/// <summary>One column's write-time layout, mirroring Etl.Core.Data.FlatFileColumnFormat's own
/// constructor shape verbatim -- this project never references the Etl.Core assembly (it emits
/// TEXT, not types), so this is codegen's own plain DTO for the same three values.</summary>
public sealed record FlatFileColumnFormatSpec(string PropertyName, int? FixedWidth, string Delimiter);

/// <summary>How one flow's rows are written out -- a plain SQL table (the existing,
/// EF-Core-backed default: AddBulkSink&lt;TEntity&gt;() resolves to SqlBulkSink&lt;TEntity&gt;) or a
/// Flat File Destination (no EF Core table at all; constructed directly with an explicit file
/// path + column layout, the same "construct directly, don't fight the DI container" pattern
/// ConditionalSplitStep's own branches already use for their transforms). Exactly one concrete
/// case per flow. <see cref="ComponentName"/> is the destination's own real SSIS component name
/// (e.g. "OLEDST_CustomerEnriched") -- every subtype carries it now (added for the 1-to-1
/// component-to-function mapping round), not just <see cref="RedirectingSqlFlowSink"/>, so
/// PackageClassEmitter.EmitSinkMethod can name a plain single-destination sink after its own real
/// component instead of the entity it lands ("{Entity}Destination"), the same traceability every
/// source method already had.</summary>
public abstract record FlowSinkSpec(string ComponentName);
public sealed record SqlFlowSink(string ComponentName) : FlowSinkSpec(ComponentName);
public sealed record FlatFileFlowSink(string ComponentName, string FileSourceKey, bool Overwrite, string? HeaderLine, IReadOnlyList<FlatFileColumnFormatSpec> Columns) : FlowSinkSpec(ComponentName);

/// <summary>A destination whose own input is configured ErrorRowDisposition=RedirectRow -- a row
/// that fails to insert is redirected to a second, named destination (ErrorEntityName/table)
/// instead of aborting the load. <see cref="PrimaryComponentName"/>/<see cref="ErrorComponentName"/>
/// are each destination's own SSIS component name (e.g. OLEDST_StagingCustomers/
/// OLEDST_StagingErrors) -- this one carries TWO component identities, because naming BOTH sink
/// methods after their own component is the only way two destinations in one flow stay
/// distinguishable; <see cref="FlowSinkSpec.ComponentName"/> (the base record's own property)
/// resolves to <see cref="PrimaryComponentName"/>. <see cref="ErrorMapClassName"/> is the
/// generated static class translating a failed TEntity + the causing DbException into the error
/// entity -- see PackageGenerator.ResolveErrorRedirectSink.</summary>
public sealed record RedirectingSqlFlowSink(string PrimaryComponentName, string ErrorComponentName, string ErrorEntityName, string ErrorMapClassName) : FlowSinkSpec(PrimaryComponentName);

/// <summary>One Data Flow Task's already-decided naming, combining PackagePlanner's
/// DataFlowPlan facts (StepName, Source) with the naming choices the caller makes for the
/// generated types (matching EntityEmitter/CsvRowEmitter|SqlRowEmitter/TransformEmitter's own
/// outputs).</summary>
public sealed record ProgramFlowSpec(
    string StepName,
    FlowSourceSpec Source,
    string RowTypeName,
    string EntityName,
    string TransformClassName,
    FlowSinkSpec Sink,
    LookupPreload? Lookup = null,
    // True for the original single-output Lookup shape, where the transform's own Map() does
    // the join itself (via LookupJoinSpec) and so needs the cache as a constructor argument.
    // False for the Lookup+Aggregate composed shape (2026-09-02): there the cache is already
    // fully consumed by AggregateFlowSource's own key selector/filter, upstream of the
    // transform entirely, so the transform is a plain parameterless TrustAllPassthroughColumns
    // mapping -- passing the cache to its constructor there would be a build error (no such
    // constructor exists).
    bool TransformNeedsLookupCache = true,
    /// <summary>Added 2026-09-02 (gap-audit Phase 3.2). Every package-variable name a
    /// <c>Microsoft.RowCount</c> in this flow's own pipeline writes to -- <see cref="Etl.Core.Data.CountingRowSource{TRow}"/>
    /// wraps <see cref="Source"/> once per entry (nested, if more than one) so the side effect a
    /// plain passthrough-column resolution would otherwise silently drop is actually reproduced.
    /// Null/empty for every flow with no RowCount, which is every flow before this round.</summary>
    IReadOnlyList<string>? RowCountVariableNames = null,
    /// <summary>Added 2026-09-02 (gap-audit Phase 3.5). A standalone Sort's own single ascending
    /// key -- <see cref="Etl.Core.Data.SortingRowSource{TRow,TKey}"/> wraps <see cref="Source"/>
    /// so the ordering a real SSIS Sort applies is actually reproduced (confirmed real, before
    /// this field existed: a standalone Sort was silently invisible, see
    /// <c>Ssis.Extract.Codegen.PackagePlanner.ResolveStandaloneSort</c>'s own doc comment). Null
    /// for every flow with no standalone Sort, which is every flow before this round.</summary>
    ProgramSortKeySpec? SortKey = null);

/// <summary>See <see cref="ProgramFlowSpec.SortKey"/>. <see cref="KeyClrType"/> is the already-
/// resolved CLR type NAME (e.g. "int"), not the whole <c>SsisPipelineType</c> -- this project's
/// codegen layer, kept as a plain DTO independent of the model project it's derived from.</summary>
public sealed record ProgramSortKeySpec(string KeyColumnName, string KeyClrType);

/// <summary>
/// A full-cache Lookup's reference table. <see cref="VariableName"/> names the field
/// <see cref="PackageClassEmitter"/> declares on the generated package class and loads ONCE at
/// the very top of <c>RunAsync</c>, before the transaction even begins, so any flow's own method
/// can simply read the field -- see that emitter's own doc comment for why top level rather than
/// a DI factory: <c>IRowTransform.Map</c> is synchronous, so a full cache has to be fully
/// materialized before any row is read regardless.
/// </summary>
public sealed record LookupPreload(
    string VariableName,
    string CacheClassName,
    string KeyClrType,
    string ReferenceKeyColumn);

/// <summary>One branch of a generated Conditional Split step -- its own (RowType, EntityName)
/// transform pair, same naming convention EntityEmitter/TransformEmitter use for a
/// single-destination flow, just once per branch instead of once per flow.</summary>
public sealed record ProgramConditionalSplitBranch(string OutputName, string EntityName, string TransformClassName);

/// <summary>One entry in a package's step sequence, in execution order -- a data flow, an
/// Execute SQL Task or File System Task, or a Conditional Split (one source routed to N branches,
/// each its own destination). <see cref="PackageClassEmitter"/> gives each one its own generated
/// method on the package class.</summary>
public abstract record ProgramStep
{
    /// <summary>Non-null when a conditional precedence constraint gates this step -- carried from
    /// <c>PackageStep.Guard</c>. <see cref="PackageClassEmitter"/> wraps the CALL SITE (not the
    /// step's own method body) in a real <c>if</c>/<c>else</c>, since that's where "run or skip"
    /// actually belongs -- every per-kind method body below is untouched by whether it happens to
    /// be gated.</summary>
    public ProgramStepGuard? Guard { get; init; }

    /// <summary>Carried from <c>PackageStep.FlowGroup</c> -- which independent SSIS branch this
    /// step belongs to, purely for deciding where to print a "// Flow N:" heading among ROOT-level
    /// steps (a step nested inside a Sequence Container is grouped by that container's own
    /// generated method instead -- see <see cref="ContainerPath"/>). See that property's own doc
    /// comment.</summary>
    public int FlowGroup { get; init; }

    /// <summary>Carried from <c>PackageStep.ContainerPath</c> (added for the emitter rewrite,
    /// see <c>Docs/Emitter-Rewrite-Plan.md</c>) -- which SSIS Sequence Container(s) this step is
    /// nested inside, root-first, empty for a root-level step. <see cref="PackageClassEmitter"/>
    /// groups steps sharing a common path prefix into their own generated method (e.g.
    /// <c>SEQ_Prepare</c>), mirroring the package's own container nesting instead of flattening
    /// it away.</summary>
    public IReadOnlyList<string> ContainerPath { get; init; } = [];

    /// <summary>Carried from <c>PackageStep.Wave</c> -- see that property's own doc comment.
    /// <see cref="PackageClassEmitter"/> groups contiguous same-Wave ROOT-level steps and, for a
    /// group of more than one, emits a genuine <c>Task.WhenAll</c> instead of sequential
    /// <c>await</c>s.</summary>
    public int Wave { get; init; }
}

/// <summary>The raw SSIS constraint expression (emitted so the generated code can log what it
/// evaluated) and the C# predicate body over the lambda parameter <c>v</c>.</summary>
public sealed record ProgramStepGuard(string SsisExpression, string CSharpPredicate);

public sealed record ProgramFlowStep(ProgramFlowSpec Flow) : ProgramStep;

/// <summary><see cref="StatementClassName"/> names the Mapping/{Name}Statement.cs class
/// SqlStatementBuilderEmitter produced for this task -- the generated method calls its own
/// BuildStatement() once, at construction time, rather than embedding the raw SQL text as an
/// anonymous string literal (see that emitter's own doc comment for why).</summary>
public sealed record ProgramSqlStep(string StepName, string StatementClassName) : ProgramStep;

/// <summary>A resolved Microsoft.ExpressionTask assignment -- see
/// <c>PackagePlanner.ExpressionStep</c>/<c>ExpressionTaskEmitter</c> for how
/// <see cref="CSharpValueExpression"/> was translated. <see cref="SsisVariableName"/> is passed
/// straight through to <c>packageVariables.Set</c> at run time.</summary>
public sealed record ProgramExpressionStep(string StepName, string SsisVariableName, string CSharpValueExpression) : ProgramStep;

/// <summary>An Execute SQL Task whose own connection manager resolves to a DIFFERENT server/
/// database than this package's primary one (see <c>PackageGenerator.ResolveSqlStep</c>'s own
/// doc comment for the real motivating case and why this needs its own step kind rather than
/// an ordinary <see cref="ProgramSqlStep"/>). <see cref="ConnectionManagerName"/> names the
/// generated <c>SecondaryConnections:&lt;name&gt;</c> appsettings.json section this task's own
/// generated method reads its connection info from. <see cref="StatementClassName"/> is the same
/// SqlStatementBuilderEmitter convention <see cref="ProgramSqlStep"/> uses.</summary>
public sealed record ProgramSecondaryConnectionSqlStep(string StepName, string ConnectionManagerName, string StatementClassName) : ProgramStep;

/// <summary>A ported Script Task (--seams). <paramref name="ClassName"/> is the generated
/// partial class ScriptTaskEmitter produced; its logic arrives as a hand-written second part.</summary>
public sealed record ProgramScriptTaskStep(string StepName, string ClassName) : ProgramStep;

/// <summary>Reuses PackagePlanner's own FileSystemActionPlan verbatim rather than a parallel
/// Program*-namespaced DTO -- unlike ProgramFlowSpec/ProgramConditionalSplitBranch, there is no
/// generator-owned NAMING decision to add on top (no entity/transform class name), just literal
/// operation/paths already fully resolved by the planner.</summary>
public sealed record ProgramFileSystemStep(string StepName, FileSystemActionPlan Action) : ProgramStep;

/// <summary>A ForEach File Enumerator loop, resolved down to everything <c>Etl.Core.Pipeline.
/// ForEachLoopStep</c> needs to construct: the enumerator config plus <see cref="StatementClassName"/>
/// -- a named, parameterized <c>{TaskName}Statement.BuildStatement(string)</c> class
/// (<see cref="SqlStatementBuilderEmitter.EmitParameterized"/>, itself built from an
/// ALREADY-TRANSLATED C# expression produced by <see cref="ForEachLoopEmitter"/> in
/// <c>PackageGenerator</c>) referencing <see cref="CurrentFileParamName"/> as its own parameter
/// name -- the generated method only ever wraps a call to it in a lambda,
/// <c>{CurrentFileParamName} => {StatementClassName}.BuildStatement({CurrentFileParamName})</c>,
/// never re-parses, re-translates, or inlines the expression itself.
/// <see cref="NameMode"/> is already the resolved <c>ForEachFileNameMode</c> enum member name
/// (e.g. "NameAndExtension"), not the raw SSIS integer -- that mapping/gap-reporting also
/// happens in <c>PackageGenerator</c>, matching every other raw-enum-to-name resolution in this
/// tool (e.g. FileSystemTaskPayload.OperationRaw -&gt; FileSystemOperation).</summary>
/// <see cref="FileSourceKey"/> (emitter rewrite phase 6) is the appsettings.json "FileSource"
/// entry key this loop's own folder is read from at runtime (<c>File(key)</c>) -- the enumerator's
/// own <c>Folder</c> is never backed by a connection manager (unlike a Flat File Source's own
/// path), so <see cref="Folder"/> itself is what PackageGenerator registers a folder-only
/// (empty-filename) FileSourceEntryRequest under, keyed by this task's own name.</summary>
public sealed record ProgramForEachFileLoopStep(
    string StepName, string Folder, string FileSpec, bool Recurse, string NameMode,
    string CurrentFileParamName, string StatementClassName, string FileSourceKey) : ProgramStep;

/// <summary>A ForEach File Enumerator loop whose body is a whole Data Flow Task -- built
/// speculatively 2026-08-30, see <c>Ssis.Extract.Codegen.PackagePlanner.ForEachFileDataFlowPlan</c>'s
/// own doc comment. Unlike <see cref="ProgramFlowSpec"/>, there is no shared, top-level
/// <c>IRowSource&lt;TRow&gt;</c> for this step at all -- the whole point is a FRESH source per
/// file, so it is constructed inline inside the lambda the generated method wraps around
/// <see cref="FilePathExpression"/> (an ALREADY-TRANSLATED C# expression, produced by
/// <c>ForEachLoopEmitter</c> in <c>PackageGenerator</c> -- reusing the exact translator
/// <see cref="ProgramForEachFileLoopStep.StatementClassName"/>'s own text was built from, since a
/// connection manager's ConnectionString expression and an Execute SQL Task's SqlStatementSource
/// expression are both just "build a string from <c>@[Namespace::Variable]</c> references" --
/// still inlined here rather than named, unlike the SQL-task case, since there is nothing yet to
/// name it against: the one evidenced instance is the bare variable reference with nothing to
/// compute)
/// referencing <see cref="CurrentFileParamName"/> as a bare identifier. <see cref="RowTypeName"/>/
/// <see cref="EntityName"/>/<see cref="TransformClassName"/> match EntityEmitter/CsvRowEmitter/
/// TransformEmitter's own outputs exactly like <see cref="ProgramFlowSpec"/>'s do -- the flow
/// body is generated the same way an ordinary single-destination CSV flow is, only the source
/// construction differs.</summary>
public sealed record ProgramForEachDataFlowLoopStep(
    string StepName, string Folder, string FileSpec, bool Recurse, string NameMode,
    string CurrentFileParamName, string FilePathExpression,
    string SourceComponentName, string RowTypeName, string EntityName, string TransformClassName,
    string FileSourceKey) : ProgramStep;

/// <summary>A <c>STOCK:FORLOOP</c> whose body is a Data Flow Task -- Phase 3 of the
/// unsupported-component-types plan, structurally the counter-driven sibling of
/// <see cref="ProgramForEachDataFlowLoopStep"/>. <see cref="CounterVariableName"/> is the
/// namespace-qualified package variable name (e.g. "User::Part") every emitted
/// <c>packageVariables.GetRequired&lt;{CounterClrTypeName}&gt;(...)</c>/<c>.Set(...)</c> call
/// reads/writes directly; <see cref="InitCSharpExpression"/> is null exactly when the container
/// declared no <c>InitExpression</c> at all, in which case the generated method emits no Init
/// call whatsoever (the counter's own design-time default, already seeded onto
/// <c>packageVariables</c> the same way a conditional-constraint guard's own variables are,
/// stands as-is). <see cref="EvalCSharpPredicate"/>/<see cref="AssignCSharpValueExpression"/> are
/// already-translated C# (<see cref="ForLoopEmitter"/>) reading/producing a value of
/// <see cref="CounterClrTypeName"/>.
///
/// <see cref="FilePathExpression"/>/<see cref="SourceComponentName"/>/<see cref="RowTypeName"/>/
/// <see cref="EntityName"/>/<see cref="TransformClassName"/> mirror
/// <see cref="ProgramForEachDataFlowLoopStep"/>'s own fields exactly -- the loop body is generated
/// the same way an ordinary single-destination CSV flow is, only the per-iteration source
/// construction (reading the counter directly off <c>packageVariables</c>, not a lambda
/// parameter -- see <c>Etl.Core.Pipeline.ForLoopStep{TRow,TEntity}</c>'s own doc comment for why)
/// differs.</summary>
public sealed record ProgramForLoopStep(
    string StepName, string CounterVariableName, string CounterClrTypeName,
    string? InitCSharpExpression, string EvalCSharpPredicate, string AssignCSharpValueExpression,
    string FilePathExpression,
    string SourceComponentName, string RowTypeName, string EntityName, string TransformClassName) : ProgramStep;

public sealed record ProgramConditionalSplitStep(
    string StepName,
    FlowSourceSpec Source,
    string RowTypeName,
    string RouterClassName,
    List<ProgramConditionalSplitBranch> Branches) : ProgramStep;

/// <summary>One branch of a generated Multicast step -- unlike <see cref="ProgramConditionalSplitBranch"/>,
/// carries its own <see cref="Sink"/> because the real evidenced Multicast shape (RBC_Demo_ETL's
/// own DFT_FixedWidthImport) fans to TWO DIFFERENT destination kinds from one component (a Flat
/// File Destination and an OLE DB Destination) -- no evidenced Conditional Split ever needed
/// this, so that record's own branches are still assumed uniformly SQL-sunk.</summary>
public sealed record ProgramMulticastBranch(string OutputName, string EntityName, string TransformClassName, FlowSinkSpec Sink);

/// <summary>One Multicast: one source, every row copied unconditionally to every branch -- no
/// IRowRouter at all, unlike <see cref="ProgramConditionalSplitStep"/>.</summary>
public sealed record ProgramMulticastStep(
    string StepName,
    FlowSourceSpec Source,
    string RowTypeName,
    List<ProgramMulticastBranch> Branches) : ProgramStep;

/// <summary>
/// One <c>Microsoft.SCD</c> output's downstream chain, as generated. Exactly one of the three shapes
/// the real evidenced package uses, distinguished by which fields are populated:
/// insert-only (<see cref="EntityName"/>/<see cref="TransformClassName"/>/<see cref="Sink"/> set),
/// command-only (<see cref="SqlTemplate"/> set), or both.
/// <see cref="Slot"/> is the <c>Etl.Core.Pipeline.ScdBranches{TRow}</c> property this branch fills.
/// </summary>
public sealed record ProgramScdBranch(
    string Slot,
    string OutputName,
    string? EntityName,
    string? TransformClassName,
    FlowSinkSpec? Sink,
    string? SqlTemplate,
    IReadOnlyList<string> CommandParameterColumns);

/// <summary>
/// A Slowly Changing Dimension flow (Phase 7 of the unsupported-component-types plan) -- one source,
/// a full-cache read of the dimension's current rows, and up to five routed branches. Unlike every
/// other multi-branch step here, a branch may run a per-row SQL command instead of (or as well as)
/// inserting, and the branches run in two ordered phases -- see
/// <c>Etl.Core.Pipeline.SlowlyChangingDimensionStep{TRow,TKey}</c>'s own doc comment for why that
/// ordering is a correctness requirement.
///
/// <para><see cref="AttributeRoles"/>, <see cref="AttributeColumns"/> and
/// <see cref="AttributeExpressions"/> are index-aligned, and all three align with the
/// <c>object?[]</c> <see cref="CacheClassName"/> produces -- that alignment is the whole contract
/// between the emitted cache, the emitted value selector, and the runtime classifier.</para>
///
/// <para><see cref="AttributeExpressions"/> carries each attribute's own ALREADY-RESOLVED C#
/// expression -- a plain <c>row.{Name}</c> passthrough for an ordinary column, or
/// <c>SsisFn.ToNullable*(row.{RawSourceColumn})</c> for one produced by an intervening Data
/// Conversion component (resolved via <c>TransformEmitter.TranslateDataConversion</c>, the same
/// helper a Conditional Split condition/Derived Column cross-reference already reuses -- see
/// <c>PackageGenerator.GenerateScdFlow</c>). <see cref="AttributeColumns"/> stays the bare SSIS
/// column name throughout (used for the dimension cache's own reference-SQL projection and for
/// human-readable gap text), never re-derived into an identifier at the point of use any more.</para>
/// </summary>
public sealed record ProgramScdStep(
    string StepName,
    FlowSourceSpec Source,
    string RowTypeName,
    string CacheClassName,
    IReadOnlyList<string> BusinessKeyColumns,
    IReadOnlyList<string> BusinessKeyClrTypes,
    IReadOnlyList<string> AttributeColumns,
    IReadOnlyList<string> AttributeExpressions,
    IReadOnlyList<string> AttributeRoles,
    bool FailOnFixedAttributeChange,
    bool UpdateChangingAttributeHistory,
    IReadOnlyList<ProgramScdBranch> Branches) : ProgramStep;

/// <summary>An OLE DB Command flow -- no destination table, no EF entity, no IBulkSink at all;
/// the command itself is the flow's sink, run once per source row via
/// Etl.Core.Pipeline.OleDbCommandStep&lt;TRow&gt;. <see cref="SqlTemplate"/> already has every
/// <c>?</c> rewritten to EF Core's own "{0}", "{1}", ... raw-SQL placeholder syntax (see
/// PackagePlanner.OleDbCommandPlan), matched positionally against <see cref="ParameterColumnNames"/>
/// (the row type's own property names, in the same order).</summary>
public sealed record ProgramOleDbCommandStep(
    string StepName,
    FlowSourceSpec Source,
    string RowTypeName,
    string SqlTemplate,
    IReadOnlyList<string> ParameterColumnNames) : ProgramStep;

/// <summary>Deprecated, always empty as of the emitter rewrite (see
/// <c>Docs/Emitter-Rewrite-Plan.md</c>) -- a pre-flow Execute SQL Task is no longer hoisted ahead
/// of every step; <c>PackagePlanner.WalkContainer</c> now emits it as an ordinary
/// <see cref="ProgramSqlStep"/> at its true topological position instead. Kept on
/// <see cref="ProgramRequest"/> only so every existing construction site (many test files) keeps
/// compiling; a future cleanup pass can drop it.</summary>
public sealed record ProgramPreLoadStatement(string TaskName, string StatementClassName);

public sealed record ProgramRequest(
    string PackageName,
    string RootNamespace,
    string DbContextTypeName,
    List<ProgramPreLoadStatement> PreLoadStatements,
    List<FileSystemActionPlan> PreLoadFileActions,
    List<ProgramStep> Steps)
{
    /// <summary>Design-time defaults of every package variable a step guard reads, emitted as
    /// explicit Set calls so the value a guard starts from is visible in the generated file rather
    /// than being whatever <c>Get&lt;T&gt;</c> happens to fall back to.</summary>
    public IReadOnlyList<PackageVariableSeed> VariableSeeds { get; init; } = [];

    /// <summary>Failure-precedence-constraint successors, run in the catch block's own
    /// try/catch-per-handler loop -- a position reached only after the transaction has already
    /// been rolled back, each isolated so a throwing handler never replaces the original
    /// exception.</summary>
    public IReadOnlyList<FailureHandlerPlan> FailureHandlers { get; init; } = [];

    /// <summary>Whether to wire <c>IPackageResultNotifier</c>/<c>AddEmailNotifications</c> at all.
    /// Defaults to false -- a .dtsx carries no notification-recipient information (see the
    /// {Package}.Notification gap), so unconditionally emitting this hook on every generated
    /// package would wire an email-sending call the ORIGINAL SSIS package never had any equivalent
    /// of, on the strength of nothing but "maybe someone will configure it later." Opt in via
    /// `ssisx generate --notifications` once real recipients/SMTP settings actually exist to
    /// configure.</summary>
    public bool IncludeNotifications { get; init; }
}

/// <summary>
/// Emits Program.cs -- the package's bootstrap ONLY. Everything about how the package actually
/// runs (constructing every step, running each one by name in order, the transaction/rollback/
/// failure-handler wrapper) lives in <see cref="PackageClassEmitter"/>'s own
/// <c>{PackageName}.cs</c> instead. See that class's own doc comment for the full account of why
/// the split happened (the emitter rewrite, <c>Docs/Emitter-Rewrite-Plan.md</c>) -- in one
/// sentence: a reader mapping a human-authored walkthrough onto a flat, 1000+-line top-level-
/// statement script had no anchor to hold onto (a container like <c>SEQ_Prepare</c> left no trace
/// at all, and every component was buried in a <c>builder.Services.AddScoped&lt;IRowSource&lt;T&gt;&gt;(sp
/// =&gt; {...})</c> DI-registration lambda far from where it actually ran); a method per SSIS
/// task/component, named after that task/component, fixes that directly.
/// </summary>
public static class ProgramEmitter
{
    public static EmitResult Emit(ProgramRequest request)
    {
        var className = PackageClassEmitter.ClassName(request.PackageName);
        var w = new CodeWriter();
        w.Line("using Etl.Core.Hosting;");
        if (request.IncludeNotifications) w.Line("using Etl.Core.Notifications;");
        w.Line($"using {request.RootNamespace};");
        w.Line($"using {request.RootNamespace}.Model;");
        w.Line("using Microsoft.Extensions.DependencyInjection;");
        w.Blank();
        w.Line($"const string PackageName = {CSharpStringLiteral(request.PackageName)};");
        w.Blank();
        w.Line("var builder = EtlHost.Create(args, PackageName);");
        w.Line($"builder.Services.AddEtlDbContext<{request.DbContextTypeName}>();");
        if (request.IncludeNotifications) w.Line("builder.Services.AddEmailNotifications(builder.Configuration);");
        w.Blank();
        w.Line("using var host = builder.Build();");
        w.Line($"return await new {className}(host.Services).RunAsync(CancellationToken.None);");

        return new EmitResult([new GeneratedFile("Program.cs", w.Render())], []);
    }

    /// <summary>Escapes backslash/quote AND control characters -- a flat file's own
    /// ColumnDelimiterDecoded/HeaderLine can contain a literal CR/LF (SSIS's decoded row
    /// delimiter, not its escape-sequence text), and a raw newline inside a non-verbatim C#
    /// string literal is a compile error (CS1010), not just cosmetically wrong.
    /// Internal, not private -- ForEachLoopEmitter/PackageClassEmitter reuse this exact escaping,
    /// same sharing precedent as TransformEmitter.CollectSsisFunctions/MapPipelineTypeToSsisType.</summary>
    internal static string CSharpStringLiteral(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        }
        sb.Append('"');
        return sb.ToString();
    }
}
