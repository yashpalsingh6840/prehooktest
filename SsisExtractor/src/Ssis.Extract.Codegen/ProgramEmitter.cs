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
public sealed record SqlFlowSource(string ComponentName, string CommandText) : FlowSourceSpec(ComponentName);

/// <summary>An Excel Source, added 2026-08-28 testing this tool against a real third-party
/// portfolio (SSIS_From_Sandeep). Uses the same <see cref="FileSourceOptions"/>/FileSourceKey
/// mechanism a <see cref="CsvFlowSource"/> does for its file path (a workbook is just another
/// named folder/filename pair), plus the worksheet name and whether to skip its own header row.
/// <see cref="WhereFilter"/> (gap-audit Phase 3.4, 2026-09-02), when set, is a C# boolean
/// expression referencing <c>row.{Column}</c> (see <see cref="ExcelWhereClauseTranslator"/>) --
/// the underlying <c>ExcelRowSource{TRow}</c> is wrapped in a <c>FilteringRowSource{TRow}</c>
/// rather than given any query capability of its own, since it never had one.</summary>
public sealed record ExcelFlowSource(string ComponentName, string FileSourceKey, string WorksheetName, bool HasHeaderRow, string? WhereFilter = null) : FlowSourceSpec(ComponentName);

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
    string GroupByPropertyName,
    string GroupByKeyClrType,
    List<AggregateFunctionFieldSpec> Functions,
    AggregateLookupKeySpec? LookupKey = null,
    (string LookupVariableName, string JoinInputPropertyName)? LookupFilter = null) : FlowSourceSpec(ComponentName);

/// <summary>One column's write-time layout, mirroring Etl.Core.Data.FlatFileColumnFormat's own
/// constructor shape verbatim -- this project never references the Etl.Core assembly (it emits
/// TEXT, not types), so this is codegen's own plain DTO for the same three values.</summary>
public sealed record FlatFileColumnFormatSpec(string PropertyName, int? FixedWidth, string Delimiter);

/// <summary>How one flow's rows are written out -- a plain SQL table (the existing,
/// EF-Core-backed default: AddBulkSink&lt;TEntity&gt;() resolves to SqlBulkSink&lt;TEntity&gt;) or a
/// Flat File Destination (no EF Core table at all; constructed directly with an explicit file
/// path + column layout, the same "construct directly, don't fight the DI container" pattern
/// ConditionalSplitStep's own branches already use for their transforms). Exactly one concrete
/// case per flow.</summary>
public abstract record FlowSinkSpec;
public sealed record SqlFlowSink : FlowSinkSpec;
public sealed record FlatFileFlowSink(string FileSourceKey, bool Overwrite, string? HeaderLine, IReadOnlyList<FlatFileColumnFormatSpec> Columns) : FlowSinkSpec;

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
/// A full-cache Lookup's reference table, loaded ONCE at Program.cs top level and captured by the
/// transform that consults it.
///
/// <b>Why at top level rather than inside a DI factory or an Etl.Core step:</b>
/// <c>IRowTransform.Map</c> is synchronous, so the cache has to be fully materialized before any
/// row is read -- which is exactly what full-cache semantics mean anyway. Program.cs is top-level
/// statements, so it can simply <c>await</c> the load before registering anything. That needs no
/// new Etl.Core abstraction and no sync-over-async, and it keeps the load visible in the generated
/// file rather than buried in a lambda that happens to run once.
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

/// <summary>One entry in a package's post-pre-load step sequence, in execution order --
/// a data flow, an Execute SQL Task or File System Task that runs after at least one data flow,
/// or a Conditional Split (one source routed to N branches, each its own destination).</summary>
public abstract record ProgramStep
{
    /// <summary>Non-null when a conditional precedence constraint gates this step -- carried from
    /// <c>PackageStep.Guard</c>. Emission wraps the step's own local in an <c>Etl.Core</c>
    /// <c>ConditionalStep</c>; every per-kind case below is untouched, since which step is gated is
    /// orthogonal to how that step is constructed.</summary>
    public ProgramStepGuard? Guard { get; init; }
}

/// <summary>The raw SSIS constraint expression (emitted so the generated code can log what it
/// evaluated) and the C# predicate body over the lambda parameter <c>v</c>.</summary>
public sealed record ProgramStepGuard(string SsisExpression, string CSharpPredicate);

public sealed record ProgramFlowStep(ProgramFlowSpec Flow) : ProgramStep;
public sealed record ProgramSqlStep(string StepName, string Sql) : ProgramStep;

/// <summary>An Execute SQL Task whose own connection manager resolves to a DIFFERENT server/
/// database than this package's primary one (see <c>PackageGenerator.ResolveSqlStep</c>'s own
/// doc comment for the real motivating case and why this needs its own step kind rather than
/// an ordinary <see cref="ProgramSqlStep"/>). <see cref="ConnectionManagerName"/> names both the
/// generated <c>SecondaryConnections:&lt;name&gt;</c> appsettings.json section this reads its
/// connection info from and the top-level connection-string local this emitter builds once per
/// distinct name referenced.</summary>
public sealed record ProgramSecondaryConnectionSqlStep(string StepName, string ConnectionManagerName, string Sql) : ProgramStep;

/// <summary>A ported Script Task (--seams). <paramref name="ClassName"/> is the generated
/// partial class ScriptTaskEmitter produced; its logic arrives as a hand-written second part.</summary>
public sealed record ProgramScriptTaskStep(string StepName, string ClassName) : ProgramStep;

/// <summary>Reuses PackagePlanner's own FileSystemActionPlan verbatim rather than a parallel
/// Program*-namespaced DTO -- unlike ProgramFlowSpec/ProgramConditionalSplitBranch, there is no
/// generator-owned NAMING decision to add on top (no entity/transform class name), just literal
/// operation/paths already fully resolved by the planner.</summary>
public sealed record ProgramFileSystemStep(string StepName, FileSystemActionPlan Action) : ProgramStep;

/// <summary>A ForEach File Enumerator loop, resolved down to everything <c>Etl.Core.Pipeline.
/// ForEachLoopStep</c> needs to construct: the enumerator config plus an ALREADY-TRANSLATED C#
/// expression (<see cref="SqlExpression"/>, produced by <see cref="ForEachLoopEmitter"/> in
/// <c>PackageGenerator</c>, not here) referencing <see cref="CurrentFileParamName"/> as a bare
/// identifier -- this emitter only ever wraps it in a lambda,
/// <c>{CurrentFileParamName} => {SqlExpression}</c>, never re-parses or re-translates it.
/// <see cref="NameMode"/> is already the resolved <c>ForEachFileNameMode</c> enum member name
/// (e.g. "NameAndExtension"), not the raw SSIS integer -- that mapping/gap-reporting also
/// happens in <c>PackageGenerator</c>, matching every other raw-enum-to-name resolution in this
/// tool (e.g. FileSystemTaskPayload.OperationRaw -&gt; FileSystemOperation).</summary>
public sealed record ProgramForEachFileLoopStep(
    string StepName, string Folder, string FileSpec, bool Recurse, string NameMode,
    string CurrentFileParamName, string SqlExpression) : ProgramStep;

/// <summary>A ForEach File Enumerator loop whose body is a whole Data Flow Task -- built
/// speculatively 2026-08-30, see <c>Ssis.Extract.Codegen.PackagePlanner.ForEachFileDataFlowPlan</c>'s
/// own doc comment. Unlike <see cref="ProgramFlowSpec"/>, there is no top-level
/// <c>IRowSource&lt;TRow&gt;</c> DI registration for this step at all -- the whole point is a
/// FRESH source per file, so it is constructed inline inside the lambda this emitter wraps
/// around <see cref="FilePathExpression"/> (an ALREADY-TRANSLATED C# expression, produced by
/// <c>ForEachLoopEmitter</c> in <c>PackageGenerator</c> -- reusing the exact translator
/// <see cref="ProgramForEachFileLoopStep.SqlExpression"/> already uses, since a connection
/// manager's ConnectionString expression and an Execute SQL Task's SqlStatementSource
/// expression are both just "build a string from <c>@[Namespace::Variable]</c> references")
/// referencing <see cref="CurrentFileParamName"/> as a bare identifier. <see cref="RowTypeName"/>/
/// <see cref="EntityName"/>/<see cref="TransformClassName"/> match EntityEmitter/CsvRowEmitter/
/// TransformEmitter's own outputs exactly like <see cref="ProgramFlowSpec"/>'s do -- the flow
/// body is generated the same way an ordinary single-destination CSV flow is, only the source
/// construction differs.</summary>
public sealed record ProgramForEachDataFlowLoopStep(
    string StepName, string Folder, string FileSpec, bool Recurse, string NameMode,
    string CurrentFileParamName, string FilePathExpression,
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

public sealed record ProgramRequest(
    string PackageName,
    string RootNamespace,
    string DbContextTypeName,
    List<string> PreLoadStatements,
    List<FileSystemActionPlan> PreLoadFileActions,
    List<ProgramStep> Steps)
{
    /// <summary>Design-time defaults of every package variable a step guard reads, emitted as
    /// explicit Set calls so the value a guard starts from is visible in the generated file rather
    /// than being whatever <c>Get&lt;T&gt;</c> happens to fall back to.</summary>
    public IReadOnlyList<PackageVariableSeed> VariableSeeds { get; init; } = [];

    /// <summary>Failure-precedence-constraint successors, emitted as EtlPackage.FailureHandlers --
    /// a position PackageRunner reaches only after rolling the package transaction back.</summary>
    public IReadOnlyList<FailureHandlerPlan> FailureHandlers { get; init; } = [];
}

/// <summary>
/// Emits one package's Program.cs. Always uses the "locals inside the IEtlPackage factory"
/// shape (matching the hand-written LoadReferenceData/Program.cs), even for a single flow --
/// per the generate plan's own Phase 1 table: "ProgramEmitter always emits the multi-flow
/// form... It works for N=1 and removes a branch from the emitter." This means a generated
/// single-flow Program.cs is NOT byte-identical to the hand-written LoadEmployees/Program.cs
/// (which uses a separate ILoadTask DI registration instead) -- a known, plan-endorsed
/// divergence, not a bug; both shapes are functionally identical, wired through the same
/// Etl.Core.Pipeline.EtlPackage/DataFlowStep types.
/// </summary>
public static class ProgramEmitter
{
    public static EmitResult Emit(ProgramRequest request)
    {
        var flows = request.Steps.OfType<ProgramFlowStep>().Select(s => s.Flow).ToList();
        var splits = request.Steps.OfType<ProgramConditionalSplitStep>().ToList();
        var multicasts = request.Steps.OfType<ProgramMulticastStep>().ToList();
        var oleDbCommands = request.Steps.OfType<ProgramOleDbCommandStep>().ToList();
        var dataFlowLoops = request.Steps.OfType<ProgramForEachDataFlowLoopStep>().ToList();
        if (flows.Count == 0 && splits.Count == 0 && multicasts.Count == 0 && oleDbCommands.Count == 0 && dataFlowLoops.Count == 0)
            return new EmitResult([], [new GenerationGap(request.PackageName, "no Data Flow Task could be planned for this package")]);

        var allSources = new List<FlowSourceSpec>();
        foreach (var source in flows.Select(f => f.Source).Concat(splits.Select(s => s.Source))
                     .Concat(multicasts.Select(m => m.Source)).Concat(oleDbCommands.Select(c => c.Source)))
        {
            allSources.Add(source);
            if (source is MergeJoinFlowSource mj) { allSources.Add(mj.LeftSource); allSources.Add(mj.RightSource); }
            if (source is AggregateFlowSource agg) allSources.Add(agg.InnerSource);
            if (source is UnionFlowSource union) allSources.AddRange(union.Sides);
        }
        // A ForEach-Data-Flow-Loop step has no top-level FlowSourceSpec at all (its own source
        // is constructed fresh, inline, per iteration -- see ProgramForEachDataFlowLoopStep's
        // own doc comment), but it is always CSV-sourced this round (PlanForEachDataFlowLoop's
        // own gate requires a Flat File Source), so it still needs the same usings/namespace.
        var usesCsv = allSources.Any(s => s is CsvFlowSource or FixedWidthFlowSource) || dataFlowLoops.Count > 0;
        var usesExcel = allSources.Any(s => s is ExcelFlowSource);
        // A Merge Join's own combined row type always lands in the .Sql namespace/folder
        // (MergeJoinEmitter's own choice, independent of what its two SIDES are sourced from),
        // and MergeJoinRowSource<>/MergeJoinType both live in Etl.Core.Data/.Abstractions -- so a
        // Merge Join flow needs these usings even when BOTH its sides happen to be CSV-sourced
        // (the real evidenced case).
        var usesSql = allSources.Any(s => s is SqlFlowSource or MergeJoinFlowSource or AggregateFlowSource or UnionFlowSource);
        // A Lookup preload needs DatabaseOptions/SqlConnectionStringFactory (Etl.Core.Data, which
        // usesSql already covers when the SOURCE is SQL -- but a Lookup flow can be CSV-sourced)
        // plus IConfiguration.Get<T>() for the binder extension method.
        var usesLookupPreload = flows.Any(f => f.Lookup is not null);
        // Same reasoning as usesLookupPreload -- IConfiguration.Get<T>() is needed to read each
        // distinct secondary connection's own SecondaryConnections:<name> section. DatabaseOptions/
        // SqlConnectionStringFactory (Etl.Core.Hosting) and SecondaryConnectionSqlStep (Etl.Core.
        // Pipeline) need no conditional using -- both namespaces are already added unconditionally.
        var secondaryConnectionNames = request.Steps.OfType<ProgramSecondaryConnectionSqlStep>()
            .Select(s => s.ConnectionManagerName).Distinct().ToList();
        var usesFlatFileSink = flows.Any(f => f.Sink is FlatFileFlowSink) || multicasts.Any(m => m.Branches.Any(b => b.Sink is FlatFileFlowSink));
        // CountingRowSource<TRow> (2026-09-02, gap-audit Phase 3.2) also lives in Etl.Core.Data.
        var usesRowCount = flows.Any(f => f.RowCountVariableNames is { Count: > 0 });
        // An Excel Source's own WHERE clause (gap-audit Phase 3.4, 2026-09-02) wraps
        // ExcelRowSource in a FilteringRowSource -- also Etl.Core.Data.
        var usesExcelWhereFilter = allSources.Any(s => s is ExcelFlowSource { WhereFilter: not null });
        // A standalone Sort (gap-audit Phase 3.5, 2026-09-02) wraps its own flow's row source in
        // SortingRowSource<TRow,TKey> -- also Etl.Core.Data.
        var usesStandaloneSort = flows.Any(f => f.SortKey is not null);
        // An OLE DB Command flow is the one flow shape with no IRowTransform/TransformEmitter
        // output at all -- a package whose ONLY flow is an OLE DB Command has no Mapping/
        // folder generated, so this using must be conditional (unlike Model, which always
        // exists: DbContextEmitter emits a table-less DbContext even then). Confirmed real: an
        // OLE DB Command-only package failed CS0234 ("namespace 'Mapping' does not exist")
        // before this fix, caught by actually building the generated project.
        var usesMapping = flows.Count > 0 || splits.Count > 0 || multicasts.Count > 0 || dataFlowLoops.Count > 0;

        var lines = new List<string> { "using Etl.Core.Abstractions;" };
        if (usesCsv) lines.Add("using Etl.Core.Csv;");
        if (usesSql || usesFlatFileSink || usesLookupPreload || usesRowCount || usesExcelWhereFilter || usesStandaloneSort) lines.Add("using Etl.Core.Data;");
        if (usesExcel) lines.Add("using Etl.Core.Excel;");
        lines.Add("using Etl.Core.Hosting;");
        lines.Add("using Etl.Core.Notifications;");
        lines.Add("using Etl.Core.Pipeline;");
        if (usesCsv) lines.Add($"using {request.RootNamespace}.Csv;");
        if (usesSql) lines.Add($"using {request.RootNamespace}.Sql;");
        if (usesExcel) lines.Add($"using {request.RootNamespace}.Excel;");
        if (usesMapping) lines.Add($"using {request.RootNamespace}.Mapping;");
        if (request.Steps.Any(step => step is ProgramScriptTaskStep)) lines.Add($"using {request.RootNamespace}.ScriptTasks;");
        lines.Add($"using {request.RootNamespace}.Model;");
        if (usesLookupPreload || secondaryConnectionNames.Count > 0) lines.Add("using Microsoft.Extensions.Configuration;");
        lines.Add("using Microsoft.Extensions.DependencyInjection;");
        lines.Add("using Microsoft.Extensions.Logging;");
        lines.Add("using Microsoft.Extensions.Options;");
        lines.Add("");
        lines.Add($"const string PackageName = \"{request.PackageName}\";");
        lines.Add("");
        lines.Add("var builder = EtlHost.Create(args, PackageName);");
        lines.Add("");
        lines.Add($"builder.Services.AddEtlDbContext<{request.DbContextTypeName}>();");

        // A Conditional Split branch's own sink is always SQL -- no evidenced package combines
        // a split with a Flat File Destination -- so every branch entity keeps the plain
        // AddBulkSink<T>() path unconditionally. A Multicast branch is NOT assumed uniformly SQL
        // the same way -- the real evidenced shape fans to both an OLE DB Destination AND a Flat
        // File Destination from one component, so its branches are partitioned by their own Sink.
        // DistinctBy(EntityName), not just Distinct() -- two Multicast branches CAN converge to
        // the same destination (a Union All), in which case they share one entityName but carry
        // two ProgramMulticastBranch records; only the first's own Sink value holds the real
        // data (see PackageGenerator.GenerateMulticastFlow's own comment), so picking any one
        // distinct-by-name entry is correct and avoids emitting the same DI registration twice.
        var allBranchEntityNames = splits.SelectMany(s => s.Branches.Select(b => b.EntityName));
        var multicastBranchesByEntity = multicasts.SelectMany(m => m.Branches).DistinctBy(b => b.EntityName).ToList();
        var multicastSqlBranchEntityNames = multicastBranchesByEntity.Where(b => b.Sink is SqlFlowSink).Select(b => b.EntityName);
        var sqlSinkEntityNames = flows.Where(f => f.Sink is SqlFlowSink).Select(f => f.EntityName)
            .Concat(allBranchEntityNames).Concat(multicastSqlBranchEntityNames)
            .Concat(dataFlowLoops.Select(l => l.EntityName)).Distinct();
        foreach (var entityName in sqlSinkEntityNames)
            lines.Add($"builder.Services.AddBulkSink<{entityName}>();");
        foreach (var flow in flows.Where(f => f.Sink is FlatFileFlowSink))
            EmitFlatFileSinkRegistration(lines, flow.EntityName, (FlatFileFlowSink)flow.Sink);
        foreach (var branch in multicastBranchesByEntity.Where(b => b.Sink is FlatFileFlowSink))
            EmitFlatFileSinkRegistration(lines, branch.EntityName, (FlatFileFlowSink)branch.Sink);
        lines.Add("builder.Services.AddEmailNotifications(builder.Configuration);");
        lines.Add("");

        // One connection string per DISTINCT secondary connection manager, built here at top
        // level (same reason as the Lookup preload's own lookupConnectionString right below --
        // no ServiceProvider exists yet) and captured by the later per-step factory lambda,
        // which only ever runs once per package execution.
        foreach (var cmName in secondaryConnectionNames)
        {
            var local = SecondaryConnectionLocalName(cmName);
            lines.Add($"var {local} = SqlConnectionStringFactory.Build(builder.Configuration.GetSection(\"SecondaryConnections:{cmName}\").Get<DatabaseOptions>()");
            lines.Add($"    ?? throw new InvalidOperationException(\"The SecondaryConnections:{cmName} configuration section is missing.\"));");
        }
        if (secondaryConnectionNames.Count > 0) lines.Add("");

        // Every Lookup's reference table is loaded here, before any registration -- see
        // LookupPreload's own doc comment for why top level and not a DI factory. The connection
        // string is rebuilt from configuration directly rather than resolved from DI, because no
        // ServiceProvider exists yet at this point in Program.cs.
        var lookups = flows.Where(f => f.Lookup is not null).Select(f => f.Lookup!).ToList();
        if (lookups.Count > 0)
        {
            lines.Add("var lookupDbOptions = builder.Configuration.GetSection(\"TargetDatabase\").Get<DatabaseOptions>()");
            lines.Add("    ?? throw new InvalidOperationException(\"The TargetDatabase configuration section is missing -- a Lookup's reference table cannot be loaded without it.\");");
            lines.Add("var lookupConnectionString = SqlConnectionStringFactory.Build(lookupDbOptions);");
            foreach (var lookup in lookups)
            {
                lines.Add($"var {lookup.VariableName} = await {lookup.CacheClassName}.LoadAsync<{lookup.KeyClrType}>(");
                lines.Add($"    lookupConnectionString, r => r.{lookup.ReferenceKeyColumn}, CancellationToken.None);");
            }
            lines.Add("");
        }

        foreach (var flow in flows)
        {
            EmitRowSourceRegistration(lines, flow.RowTypeName, flow.Source);
            if (flow.Lookup is { } flowLookup && flow.TransformNeedsLookupCache)
            {
                // Constructed directly rather than registered by type: the transform takes the
                // preloaded cache, which is a local, not a DI service.
                lines.Add($"builder.Services.AddScoped<IRowTransform<{flow.RowTypeName}, {flow.EntityName}>>(sp => new {flow.TransformClassName}({flowLookup.VariableName}));");
            }
            else
            {
                lines.Add($"builder.Services.AddScoped<IRowTransform<{flow.RowTypeName}, {flow.EntityName}>, {flow.TransformClassName}>();");
            }
            lines.Add("");
        }

        foreach (var split in splits)
        {
            EmitRowSourceRegistration(lines, split.RowTypeName, split.Source);
            lines.Add($"builder.Services.AddScoped<IRowRouter<{split.RowTypeName}>, {split.RouterClassName}>();");
            lines.Add("");
        }

        // Multicast has no IRowRouter to register -- every row goes to every branch
        // unconditionally, so only the shared row source is a DI service.
        foreach (var multicast in multicasts)
            EmitRowSourceRegistration(lines, multicast.RowTypeName, multicast.Source);

        foreach (var cmd in oleDbCommands)
            EmitRowSourceRegistration(lines, cmd.RowTypeName, cmd.Source);

        // No EmitRowSourceRegistration call for a ForEach-Data-Flow-Loop -- deliberately: there
        // is no single IRowSource<TRow> to register up front, since a fresh one is constructed
        // per iteration inline (see the ProgramForEachDataFlowLoopStep case below). Only its own
        // IRowTransform is a DI service, same registration shape a plain flow's own transform
        // gets.
        foreach (var loop in dataFlowLoops)
        {
            lines.Add($"builder.Services.AddScoped<IRowTransform<{loop.RowTypeName}, {loop.EntityName}>, {loop.TransformClassName}>();");
            lines.Add("");
        }

        lines.Add("builder.Services.AddScoped<IEtlPackage>(sp =>");
        lines.Add("{");
        var orderedLocalNames = new List<string>();

        // ONE PackageVariables for the whole run, shared by every Script Task step -- that
        // sharing IS the feature (RBC_Demo_ETL has one Script Task stamping
        // User::BatchStartTime and another reading it back). Declared here rather than
        // registered in DI so the sharing is visible in the generated file instead of implied
        // by a lifetime.
        // Also emitted when any step is GATED, not just when a Script Task exists: a conditional
        // precedence constraint's own condition reads variables from this same bag, and a package
        // can have a gated step with no Script Task at all. Also emitted when any flow has a
        // RowCount to reproduce (2026-09-02, gap-audit Phase 3.2) -- CountingRowSource<TRow>
        // needs the same shared bag, wrapped inline at the DataFlowStep construction site below
        // (not via its own DI registration -- IRowSource<TRow> is registered earlier, in a
        // separate factory that has no access to this lambda-scoped local).
        if (request.Steps.Any(step => step is ProgramScriptTaskStep || step.Guard is not null
            || (step is ProgramFlowStep { Flow.RowCountVariableNames.Count: > 0 })))
        {
            lines.Add("    var packageVariables = new PackageVariables();");
            foreach (var seed in request.VariableSeeds)
                lines.Add($"    packageVariables.Set(\"{seed.SsisName}\", {seed.CSharpLiteral}); // design-time default from the .dtsx");
            lines.Add("");
        }
        foreach (var step in request.Steps)
        {
            var localsBefore = orderedLocalNames.Count;
            switch (step)
            {
                case ProgramFlowStep { Flow: var flow }:
                    var flowLocal = FlowLocalName(flow.EntityName);
                    orderedLocalNames.Add(flowLocal);
                    // A RowCount's own count is only meaningful once the source is exhausted, so
                    // it wraps the row source itself (nested, one CountingRowSource per RowCount)
                    // rather than the sink -- every row still reaches the transform/sink exactly
                    // as before, this only adds counting on the way through.
                    var rowSourceExpr = $"sp.GetRequiredService<IRowSource<{flow.RowTypeName}>>()";
                    // A standalone Sort's own ordering, applied closest to the raw source --
                    // before RowCount, matching the one evidenced shape (a Sort with no RowCount
                    // in the same flow; the two are mutually exclusive today, so ordering between
                    // them is otherwise moot).
                    if (flow.SortKey is { } sortKey)
                        rowSourceExpr = $"new SortingRowSource<{flow.RowTypeName}, {sortKey.KeyClrType}>(\"{flow.StepName}.Sort\", {rowSourceExpr}, row => row.{sortKey.KeyColumnName}, Comparer<{sortKey.KeyClrType}>.Default)";
                    foreach (var variableName in flow.RowCountVariableNames ?? [])
                        rowSourceExpr = $"new CountingRowSource<{flow.RowTypeName}>(\"{flow.StepName}.RowCount\", {rowSourceExpr}, packageVariables, \"{variableName}\")";
                    lines.Add($"    var {flowLocal} = new DataFlowStep<{flow.RowTypeName}, {flow.EntityName}>(");
                    lines.Add($"        \"{flow.StepName}\",");
                    lines.Add($"        {rowSourceExpr},");
                    lines.Add($"        sp.GetRequiredService<IRowTransform<{flow.RowTypeName}, {flow.EntityName}>>(),");
                    lines.Add($"        sp.GetRequiredService<IBulkSink<{flow.EntityName}>>(),");
                    lines.Add($"        sp.GetRequiredService<ILogger<DataFlowStep<{flow.RowTypeName}, {flow.EntityName}>>>());");
                    lines.Add("");
                    break;
                case ProgramSqlStep sqlStep:
                    var sqlLocal = SqlStepLocalName(sqlStep.StepName);
                    orderedLocalNames.Add(sqlLocal);
                    lines.Add($"    var {sqlLocal} = new ExecuteSqlStep(\"{sqlStep.StepName}\", {CSharpStringLiteral(sqlStep.Sql)}, sp.GetRequiredService<ILogger<ExecuteSqlStep>>());");
                    lines.Add("");
                    break;
                case ProgramSecondaryConnectionSqlStep secondaryStep:
                    var secondaryLocal = SqlStepLocalName(secondaryStep.StepName); // same lowercase-first convention, generic enough to reuse
                    orderedLocalNames.Add(secondaryLocal);
                    lines.Add($"    var {secondaryLocal} = new SecondaryConnectionSqlStep(\"{secondaryStep.StepName}\", \"{secondaryStep.ConnectionManagerName}\", {SecondaryConnectionLocalName(secondaryStep.ConnectionManagerName)}, {CSharpStringLiteral(secondaryStep.Sql)}, sp.GetRequiredService<ILogger<SecondaryConnectionSqlStep>>());");
                    lines.Add("");
                    break;
                case ProgramScriptTaskStep scriptStep:
                    var scriptLocal = SqlStepLocalName(scriptStep.StepName); // same lowercase-first convention, generic enough to reuse
                    orderedLocalNames.Add(scriptLocal);
                    lines.Add($"    var {scriptLocal} = new {scriptStep.ClassName}(packageVariables, sp);");
                    lines.Add("");
                    break;
                case ProgramFileSystemStep fsStep:
                    var fsLocal = SqlStepLocalName(fsStep.StepName); // same lowercase-first convention, generic enough to reuse
                    orderedLocalNames.Add(fsLocal);
                    lines.Add($"    var {fsLocal} = new FileSystemStep(\"{fsStep.StepName}\", {FileSystemActionExpr(fsStep.Action)}, sp.GetRequiredService<ILogger<FileSystemStep>>());");
                    lines.Add("");
                    break;
                case ProgramForEachFileLoopStep loopStep:
                    var loopLocal = SqlStepLocalName(loopStep.StepName); // same lowercase-first convention, generic enough to reuse
                    orderedLocalNames.Add(loopLocal);
                    lines.Add($"    var {loopLocal} = new ForEachLoopStep(");
                    lines.Add($"        \"{loopStep.StepName}\",");
                    lines.Add($"        new ForEachFileLoopAction({CSharpStringLiteral(loopStep.Folder)}, {CSharpStringLiteral(loopStep.FileSpec)}, {(loopStep.Recurse ? "true" : "false")}, ForEachFileNameMode.{loopStep.NameMode}),");
                    lines.Add($"        {loopStep.CurrentFileParamName} => {loopStep.SqlExpression},");
                    lines.Add("        sp.GetRequiredService<ILogger<ForEachLoopStep>>());");
                    lines.Add("");
                    break;
                case ProgramForEachDataFlowLoopStep dfLoopStep:
                    var dfLoopLocal = SqlStepLocalName(dfLoopStep.StepName); // same lowercase-first convention, generic enough to reuse
                    orderedLocalNames.Add(dfLoopLocal);
                    lines.Add($"    var {dfLoopLocal} = new ForEachFileDataFlowStep<{dfLoopStep.RowTypeName}, {dfLoopStep.EntityName}>(");
                    lines.Add($"        \"{dfLoopStep.StepName}\",");
                    lines.Add($"        new ForEachFileLoopAction({CSharpStringLiteral(dfLoopStep.Folder)}, {CSharpStringLiteral(dfLoopStep.FileSpec)}, {(dfLoopStep.Recurse ? "true" : "false")}, ForEachFileNameMode.{dfLoopStep.NameMode}),");
                    lines.Add($"        {dfLoopStep.CurrentFileParamName} => new CsvRowSource<{dfLoopStep.RowTypeName}>({CSharpStringLiteral(dfLoopStep.SourceComponentName)}, new CsvSourceOptions {{ FilePath = {dfLoopStep.FilePathExpression} }}, new {dfLoopStep.RowTypeName}Map()),");
                    lines.Add($"        sp.GetRequiredService<IRowTransform<{dfLoopStep.RowTypeName}, {dfLoopStep.EntityName}>>(),");
                    lines.Add($"        sp.GetRequiredService<IBulkSink<{dfLoopStep.EntityName}>>(),");
                    lines.Add($"        sp.GetRequiredService<ILogger<DataFlowStep<{dfLoopStep.RowTypeName}, {dfLoopStep.EntityName}>>>());");
                    lines.Add("");
                    break;
                case ProgramConditionalSplitStep splitStep:
                    var splitLocal = SplitStepLocalName(splitStep.StepName);
                    orderedLocalNames.Add(splitLocal);
                    lines.Add($"    var {splitLocal} = new ConditionalSplitStep<{splitStep.RowTypeName}>(");
                    lines.Add($"        \"{splitStep.StepName}\",");
                    lines.Add($"        sp.GetRequiredService<IRowSource<{splitStep.RowTypeName}>>(),");
                    lines.Add($"        sp.GetRequiredService<IRowRouter<{splitStep.RowTypeName}>>(),");
                    lines.Add("        [");
                    for (var i = 0; i < splitStep.Branches.Count; i++)
                    {
                        var branch = splitStep.Branches[i];
                        var comma = i < splitStep.Branches.Count - 1 ? "," : "";
                        lines.Add($"            new ConditionalSplitBranch<{splitStep.RowTypeName}, {branch.EntityName}>(");
                        lines.Add($"                \"{branch.OutputName}\",");
                        // Constructed directly, not via sp.GetRequiredService<IRowTransform<...>>() --
                        // two or more branches can share EntityName (a Union All remerge, see
                        // PackagePlanner.ResolveBranch/PackageGenerator.GenerateConditionalSplitFlow),
                        // in which case they'd all close over the SAME generic IRowTransform<TRow,TEntity>
                        // registration; the built-in DI container resolves GetRequiredService<T>() to
                        // the LAST one registered for a duplicated service type rather than throwing, so
                        // every such branch would silently run whichever transform happened to register
                        // last. A generated transform class never has constructor dependencies (see
                        // TransformEmitter.BuildFile), so `new` is always safe here -- simpler than a
                        // keyed-DI registration, and correct for every branch shape, not just colliding
                        // ones.
                        lines.Add($"                new {branch.TransformClassName}(),");
                        lines.Add($"                sp.GetRequiredService<IBulkSink<{branch.EntityName}>>()){comma}");
                    }
                    lines.Add("        ],");
                    lines.Add($"        sp.GetRequiredService<ILogger<ConditionalSplitStep<{splitStep.RowTypeName}>>>());");
                    lines.Add("");
                    break;
                case ProgramMulticastStep multicastStep:
                    var multicastLocal = SplitStepLocalName(multicastStep.StepName); // same lowercase-first convention, generic enough to reuse
                    orderedLocalNames.Add(multicastLocal);
                    lines.Add($"    var {multicastLocal} = new MulticastStep<{multicastStep.RowTypeName}>(");
                    lines.Add($"        \"{multicastStep.StepName}\",");
                    lines.Add($"        sp.GetRequiredService<IRowSource<{multicastStep.RowTypeName}>>(),");
                    lines.Add("        [");
                    for (var i = 0; i < multicastStep.Branches.Count; i++)
                    {
                        var branch = multicastStep.Branches[i];
                        var comma = i < multicastStep.Branches.Count - 1 ? "," : "";
                        // Same "construct the transform directly, resolve the sink via DI" split
                        // as ConditionalSplitStep's own branches, for the exact same reason (a
                        // shared entity name across branches would otherwise collide on the
                        // generic IRowTransform<TRow,TEntity> DI registration) -- IBulkSink<TEntity>
                        // resolves correctly via DI regardless of whether it's a SqlBulkSink or a
                        // FlatFileBulkSink, since both registration paths add the same service type.
                        lines.Add($"            new ConditionalSplitBranch<{multicastStep.RowTypeName}, {branch.EntityName}>(");
                        lines.Add($"                \"{branch.OutputName}\",");
                        lines.Add($"                new {branch.TransformClassName}(),");
                        lines.Add($"                sp.GetRequiredService<IBulkSink<{branch.EntityName}>>()){comma}");
                    }
                    lines.Add("        ],");
                    lines.Add($"        sp.GetRequiredService<ILogger<MulticastStep<{multicastStep.RowTypeName}>>>());");
                    lines.Add("");
                    break;
                case ProgramOleDbCommandStep cmdStep:
                    var cmdLocal = SqlStepLocalName(cmdStep.StepName); // same lowercase-first convention, generic enough to reuse
                    orderedLocalNames.Add(cmdLocal);
                    lines.Add($"    var {cmdLocal} = new OleDbCommandStep<{cmdStep.RowTypeName}>(");
                    lines.Add($"        \"{cmdStep.StepName}\",");
                    lines.Add($"        sp.GetRequiredService<IRowSource<{cmdStep.RowTypeName}>>(),");
                    lines.Add($"        {CSharpStringLiteral(cmdStep.SqlTemplate)},");
                    lines.Add($"        row => new object?[] {{ {string.Join(", ", cmdStep.ParameterColumnNames.Select(c => $"row.{c}"))} }},");
                    lines.Add($"        sp.GetRequiredService<ILogger<OleDbCommandStep<{cmdStep.RowTypeName}>>>());");
                    lines.Add("");
                    break;
            }

            // A gated step is wrapped in its own second local rather than inline in the Steps list:
            // it keeps the wrapper readable, and it works uniformly for every step kind above
            // without any of them knowing about guards. Every case adds exactly one local, so the
            // one just added is the step to wrap -- checked rather than assumed.
            if (step.Guard is { } guard && orderedLocalNames.Count == localsBefore + 1)
            {
                var inner = orderedLocalNames[^1];
                var gatedLocal = $"{inner}Gated";
                orderedLocalNames[^1] = gatedLocal;
                lines.Add($"    var {gatedLocal} = new ConditionalStep(");
                lines.Add($"        {inner},");
                lines.Add($"        {CSharpStringLiteral(guard.SsisExpression)},");
                lines.Add($"        v => {guard.CSharpPredicate},");
                lines.Add("        packageVariables,");
                lines.Add("        sp.GetRequiredService<ILogger<ConditionalStep>>());");
                lines.Add("");
            }
        }
        lines.Add("    return new EtlPackage(");
        lines.Add("        Name: PackageName,");
        lines.Add($"        PreLoadStatements: [{string.Join(", ", request.PreLoadStatements.Select(CSharpStringLiteral))}],");
        lines.Add($"        Steps: [{string.Join(", ", orderedLocalNames)}],");
        // FailureHandlers is emitted only when there IS one -- it is an optional EtlPackage
        // parameter defaulting to empty, so adding "FailureHandlers: []" to every package would
        // change the generated text of every existing package for no behavioural difference.
        var fileActions = $"        PreLoadFileActions: [{string.Join(", ", request.PreLoadFileActions.Select(FileSystemActionExpr))}]";
        if (request.FailureHandlers.Count == 0)
        {
            lines.Add(fileActions + ");");
        }
        else
        {
            lines.Add(fileActions + ",");
            lines.Add($"        FailureHandlers: [{string.Join(", ", request.FailureHandlers.Select(FailureHandlerExpr))}]);");
        }
        lines.Add("});");
        lines.Add("");

        lines.Add("using var host = builder.Build();");
        lines.Add("");
        lines.Add("await using var scope = host.Services.CreateAsyncScope();");
        lines.Add("var runner = scope.ServiceProvider.GetRequiredService<IPackageRunner>();");
        lines.Add("var package = scope.ServiceProvider.GetRequiredService<IEtlPackage>();");
        lines.Add("var notifier = scope.ServiceProvider.GetRequiredService<IPackageResultNotifier>();");
        lines.Add("var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();");
        lines.Add("");
        lines.Add("logger.LogInformation(\"Starting {Package}...\", package.Name);");
        lines.Add("var result = await runner.RunAsync(package, CancellationToken.None);");
        lines.Add("await notifier.NotifyAsync(result, CancellationToken.None);");
        lines.Add("");
        lines.Add("if (result.Succeeded)");
        lines.Add("{");
        lines.Add("    foreach (var step in result.Steps)");
        lines.Add("        logger.LogInformation(\"{Step}: {Rows:N0} rows loaded\", step.Name, step.RowsWritten);");
        lines.Add("    return ExitCode.Success;");
        lines.Add("}");
        lines.Add("");
        lines.Add("logger.LogError(result.Error, \"{Package} failed\", package.Name);");
        lines.Add("return ExitCode.LoadFailed;");

        return new EmitResult([new GeneratedFile("Program.cs", Rendering.JoinLines(lines))], []);
    }

    /// <summary>"Employee" -> "employeeFlow", matching the hand-written LoadReferenceData's own
    /// departmentFlow/designationFlow local names.</summary>
    private static string FlowLocalName(string entityName) =>
        char.ToLowerInvariant(entityName[0]) + entityName[1..] + "Flow";

    /// <summary>"SQL_UpdateLoadA" -> "sQL_UpdateLoadAStep" -- same lowercase-first-letter
    /// convention as FlowLocalName; SSIS task names are already valid C# identifier text
    /// (letters/digits/underscore), so no further sanitizing is needed.</summary>
    private static string SqlStepLocalName(string taskName) =>
        char.ToLowerInvariant(taskName[0]) + taskName[1..] + "Step";

    /// <summary>Same convention as SqlStepLocalName -- "CM_SQL_SSISDemoCache" ->
    /// "cM_SQL_SSISDemoCacheConnectionString".</summary>
    private static string SecondaryConnectionLocalName(string connectionManagerName) =>
        char.ToLowerInvariant(connectionManagerName[0]) + connectionManagerName[1..] + "ConnectionString";

    /// <summary>e.g. <c>new FileSystemPreLoadAction(FileSystemOperation.Copy, "C:\...", "D:\...", true)</c> -- shared by pre-load emission and the post-flow ProgramFileSystemStep case, since both need the exact same literal expression.</summary>
    /// <summary>One Etl.Core FailureHandlerAction literal. Sibling of <see cref="FileSystemActionExpr"/>
    /// -- same "plain data record, emitted inline in the EtlPackage construction" shape.</summary>
    private static string FailureHandlerExpr(FailureHandlerPlan handler) =>
        $"new FailureHandlerAction({CSharpStringLiteral(handler.TaskName)}, {CSharpStringLiteral(handler.Sql)})";

    private static string FileSystemActionExpr(FileSystemActionPlan action) =>
        $"new FileSystemPreLoadAction(FileSystemOperation.{action.Operation}, {CSharpStringLiteral(action.SourcePath)}, {(action.DestinationPath is null ? "null" : CSharpStringLiteral(action.DestinationPath))}, {(action.Overwrite ? "true" : "false")})";

    /// <summary>Same convention as SqlStepLocalName -- "CSPL_HighLow" -> "cSPL_HighLowSplit".</summary>
    private static string SplitStepLocalName(string taskName) =>
        char.ToLowerInvariant(taskName[0]) + taskName[1..] + "Split";

    /// <summary>Shared by both ProgramFlowStep and ProgramConditionalSplitStep -- a Conditional
    /// Split reads from exactly the same kind of IRowSource&lt;TRow&gt; a single-destination flow
    /// does, so this is the one place that switch is written.</summary>
    private static void EmitRowSourceRegistration(List<string> lines, string rowTypeName, FlowSourceSpec source)
    {
        lines.Add($"builder.Services.AddScoped<IRowSource<{rowTypeName}>>(sp =>");
        lines.Add("{");
        switch (source)
        {
            case MergeJoinFlowSource mj:
                // Left/Right are each built via a local function (still closing over the outer
                // `sp`) rather than a second AddScoped<IRowSource<...>> registration -- neither
                // side's own IRowSource is a real, independently-useful DI service; it exists
                // only to feed this one MergeJoinRowSource.
                lines.Add($"    IRowSource<{mj.LeftRowTypeName}> BuildLeft()");
                lines.Add("    {");
                EmitSourceConstruction(lines, "        ", mj.LeftRowTypeName, mj.LeftSource);
                lines.Add("    }");
                lines.Add("");
                lines.Add($"    IRowSource<{mj.RightRowTypeName}> BuildRight()");
                lines.Add("    {");
                EmitSourceConstruction(lines, "        ", mj.RightRowTypeName, mj.RightSource);
                lines.Add("    }");
                lines.Add("");
                lines.Add($"    return new MergeJoinRowSource<{mj.LeftRowTypeName}, {mj.RightRowTypeName}, {mj.KeyClrType}, {rowTypeName}>(");
                lines.Add($"        {CSharpStringLiteral(mj.ComponentName)},");
                lines.Add("        BuildLeft(), BuildRight(),");
                lines.Add($"        {mj.MapperClassName}.LeftKey, {mj.MapperClassName}.RightKey,");
                lines.Add($"        Comparer<{mj.KeyClrType}>.Default,");
                lines.Add($"        MergeJoinType.{mj.JoinType},");
                lines.Add($"        {mj.MapperClassName}.Map);");
                break;
            case CsvFlowSource csv:
                EmitSourceConstruction(lines, "    ", rowTypeName, csv);
                break;
            case FixedWidthFlowSource fw:
                EmitSourceConstruction(lines, "    ", rowTypeName, fw);
                break;
            case SqlFlowSource sql:
                EmitSourceConstruction(lines, "    ", rowTypeName, sql);
                break;
            case ExcelFlowSource excel:
                EmitSourceConstruction(lines, "    ", rowTypeName, excel);
                break;
            case AggregateFlowSource agg:
                EmitSourceConstruction(lines, "    ", rowTypeName, agg);
                break;
            case UnionFlowSource union:
                // Every side built via its own local function (still closing over the outer
                // `sp`), same "no side's own IRowSource is independently useful" reasoning
                // MergeJoinFlowSource's own Left/Right already established -- each exists only
                // to feed the one MergeInterleaveRowSource/ConcatenatingRowSource below.
                for (var i = 0; i < union.Sides.Count; i++)
                {
                    lines.Add($"    IRowSource<{union.RowTypeName}> BuildSide{i}()");
                    lines.Add("    {");
                    EmitSourceConstruction(lines, "        ", union.RowTypeName, union.Sides[i]);
                    lines.Add("    }");
                    lines.Add("");
                }

                if (union.IsSortedInterleave)
                {
                    // Microsoft.Merge is hard-capped at exactly 2 inputs -- PackagePlanner's own
                    // PlanUnion already gapped out anything else, so BuildSide0()/BuildSide1()
                    // are always both present here.
                    lines.Add($"    return new MergeInterleaveRowSource<{union.RowTypeName}, {union.KeyClrType}>(");
                    lines.Add($"        {CSharpStringLiteral(union.ComponentName)}, BuildSide0(), BuildSide1(),");
                    lines.Add($"        row => row.{union.KeyPropertyName}, Comparer<{union.KeyClrType}>.Default);");
                }
                else
                {
                    var sideCalls = string.Join(", ", Enumerable.Range(0, union.Sides.Count).Select(i => $"BuildSide{i}()"));
                    lines.Add($"    return new ConcatenatingRowSource<{union.RowTypeName}>({CSharpStringLiteral(union.ComponentName)}, [{sideCalls}]);");
                }
                break;
        }
        lines.Add("});");
        lines.Add("");
    }

    /// <summary>Emits the "construct and return one IRowSource&lt;TRow&gt;" body shared by a
    /// plain single-source flow's own top-level registration and (nested, as a local function)
    /// each side of a <see cref="MergeJoinFlowSource"/>.</summary>
    private static void EmitSourceConstruction(List<string> lines, string indent, string rowTypeName, FlowSourceSpec source)
    {
        switch (source)
        {
            case CsvFlowSource csv:
                lines.Add($"{indent}var fileOptions = sp.GetRequiredService<IOptions<FileSourceOptions>>().Value;");
                lines.Add(csv.SkipRows == 0
                    ? $"{indent}var csvOptions = new CsvSourceOptions {{ FilePath = fileOptions[\"{csv.FileSourceKey}\"].ResolvedPath }};"
                    : $"{indent}var csvOptions = new CsvSourceOptions {{ FilePath = fileOptions[\"{csv.FileSourceKey}\"].ResolvedPath, SkipRows = {csv.SkipRows} }};");
                lines.Add($"{indent}return new CsvRowSource<{rowTypeName}>(\"{csv.ComponentName}\", csvOptions, new {rowTypeName}Map());");
                break;
            case FixedWidthFlowSource fw:
                lines.Add($"{indent}var fileOptions = sp.GetRequiredService<IOptions<FileSourceOptions>>().Value;");
                lines.Add($"{indent}var fixedWidthOptions = new FixedWidthSourceOptions {{ FilePath = fileOptions[\"{fw.FileSourceKey}\"].ResolvedPath, SkipRows = {fw.SkipRows} }};");
                lines.Add($"{indent}var fixedWidthColumns = new FixedWidthColumnFormat[]");
                lines.Add($"{indent}{{");
                foreach (var col in fw.Columns)
                {
                    var widthArg = col.FixedWidth is int w ? w.ToString() : "null";
                    lines.Add($"{indent}    new FixedWidthColumnFormat({CSharpStringLiteral(col.PropertyName)}, {widthArg}),");
                }
                lines.Add($"{indent}}};");
                lines.Add($"{indent}return new FixedWidthRowSource<{rowTypeName}>(\"{fw.ComponentName}\", fixedWidthOptions, fixedWidthColumns, {rowTypeName}Reader.Read);");
                break;
            case SqlFlowSource sql:
                lines.Add($"{indent}var dbOptions = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;");
                lines.Add($"{indent}var sqlOptions = new SqlSourceOptions {{ ConnectionString = SqlConnectionStringFactory.Build(dbOptions), CommandText = {CSharpStringLiteral(sql.CommandText)} }};");
                // The IUnitOfWork is passed through so this source can bind its own connection
                // to the package's active transaction (see SqlRowSource's own doc comment) --
                // without it, a later flow reading a table an earlier flow in the same run just
                // wrote (still uncommitted) deadlocks, confirmed by a real generated run.
                lines.Add($"{indent}return new SqlRowSource<{rowTypeName}>(\"{sql.ComponentName}\", sqlOptions, {rowTypeName}Reader.Read, sp.GetRequiredService<IUnitOfWork>());");
                break;
            case ExcelFlowSource excel:
                lines.Add($"{indent}var fileOptions = sp.GetRequiredService<IOptions<FileSourceOptions>>().Value;");
                lines.Add($"{indent}var excelOptions = new ExcelSourceOptions {{ FilePath = fileOptions[\"{excel.FileSourceKey}\"].ResolvedPath, WorksheetName = {CSharpStringLiteral(excel.WorksheetName)}, HasHeaderRow = {(excel.HasHeaderRow ? "true" : "false")} }};");
                if (excel.WhereFilter is { } whereFilter)
                {
                    lines.Add($"{indent}return new FilteringRowSource<{rowTypeName}>({CSharpStringLiteral(excel.ComponentName)}, new ExcelRowSource<{rowTypeName}>({CSharpStringLiteral(excel.ComponentName)}, excelOptions, {rowTypeName}Reader.Read), row => {whereFilter});");
                }
                else
                {
                    lines.Add($"{indent}return new ExcelRowSource<{rowTypeName}>({CSharpStringLiteral(excel.ComponentName)}, excelOptions, {rowTypeName}Reader.Read);");
                }
                break;
            case AggregateFlowSource agg:
                // Same "build the inner source via a local function" shape MergeJoinFlowSource's
                // own two sides already use -- the inner IRowSource<TSourceRow> is not a real,
                // independently-useful DI service; it exists only to feed this one
                // AggregateRowSource. Built speculatively 2026-08-30.
                lines.Add($"{indent}IRowSource<{agg.SourceRowTypeName}> BuildAggregateSource()");
                lines.Add($"{indent}{{");
                if (agg.LookupFilter is { } lookupFilter)
                {
                    // The real evidenced Lookup+Aggregate composed shape (2026-09-02): the raw
                    // source is built via its own nested local function first, then wrapped so a
                    // Lookup miss (NoMatchBehavior=1/redirect) never reaches grouping at all --
                    // see FilteringRowSource's own doc comment for why filtering, not a null key,
                    // is the only correct option here. Independent of LookupKey below -- a plain
                    // passthrough GroupBy column downstream of a redirect-on-miss Lookup still
                    // needs this filter even when the key selector itself needs no lookup at all.
                    lines.Add($"{indent}    IRowSource<{agg.SourceRowTypeName}> BuildRawSource()");
                    lines.Add($"{indent}    {{");
                    EmitSourceConstruction(lines, indent + "        ", agg.SourceRowTypeName, agg.InnerSource);
                    lines.Add($"{indent}    }}");
                    lines.Add($"{indent}");
                    lines.Add($"{indent}    return new FilteringRowSource<{agg.SourceRowTypeName}>({CSharpStringLiteral(agg.ComponentName)}, BuildRawSource(), row => {lookupFilter.LookupVariableName}.ContainsKey(row.{lookupFilter.JoinInputPropertyName}));");
                }
                else
                {
                    EmitSourceConstruction(lines, indent + "    ", agg.SourceRowTypeName, agg.InnerSource);
                }
                lines.Add($"{indent}}}");
                lines.Add($"{indent}");
                lines.Add($"{indent}return new AggregateRowSource<{agg.SourceRowTypeName}, {agg.GroupByKeyClrType}, {rowTypeName}>(");
                lines.Add($"{indent}    {CSharpStringLiteral(agg.ComponentName)},");
                lines.Add($"{indent}    BuildAggregateSource(),");
                lines.Add(agg.LookupKey is { } lk
                    ? $"{indent}    row => {lk.LookupVariableName}[row.{lk.JoinInputPropertyName}].{lk.ReferenceColumnName},"
                    : $"{indent}    row => row.{agg.GroupByPropertyName},");
                lines.Add($"{indent}    (key, rows) => new {rowTypeName}");
                lines.Add($"{indent}    {{");
                lines.Add($"{indent}        {agg.GroupByPropertyName} = key,");
                for (var i = 0; i < agg.Functions.Count; i++)
                {
                    var fn = agg.Functions[i];
                    var comma = i < agg.Functions.Count - 1 ? "," : "";
                    var expr = EmitAggregateFunctionExpression(fn);
                    lines.Add($"{indent}        {fn.OutputPropertyName} = {expr}{comma}");
                }
                lines.Add($"{indent}    }});");
                break;
        }
    }

    /// <summary>Renders one Aggregate function column's own LINQ expression over a group's
    /// buffered <c>rows</c> -- raw-value mapping and every NULL-handling rule measured via a real
    /// dtexec run, see <c>Ssis.Extract.Model.Pipeline.AggregatePayload</c>'s own doc comment.
    /// Count(1)/CountDistinct(3)/CountAll(2) never null; Average(5)/Minimum(6)/Maximum(7) reuse
    /// .NET's own nullable-sequence overloads UNCHANGED because they already return null for an
    /// all-null/empty group, matching SSIS exactly -- confirmed by a dedicated probe, not
    /// assumed. Sum(4) is the one exception: .NET's own <c>Enumerable.Sum</c> over a nullable
    /// sequence returns 0 for an all-null/empty group (independently verified this same round,
    /// NOT an SSIS behavior), so it needs an explicit guard to match SSIS's own NULL result.</summary>
    private static string EmitAggregateFunctionExpression(AggregateFunctionFieldSpec fn) => fn.AggregationTypeRaw switch
    {
        1 => $"rows.Count(r => r.{fn.SourcePropertyName} != null)",
        2 => "rows.Count",
        3 => $"rows.Select(r => r.{fn.SourcePropertyName}).Where(v => v != null).Distinct().Count()",
        4 => $"rows.All(r => r.{fn.SourcePropertyName} == null) ? null : rows.Sum(r => r.{fn.SourcePropertyName})",
        5 => $"rows.Average(r => r.{fn.SourcePropertyName})",
        6 => $"rows.Min(r => r.{fn.SourcePropertyName})",
        7 => $"rows.Max(r => r.{fn.SourcePropertyName})",
        // PackagePlanner.PlanAggregate already rejects any other raw value before this is ever
        // reached -- this can only fire on a genuine internal inconsistency between the two.
        _ => throw new InvalidOperationException($"unsupported AggregationType {fn.AggregationTypeRaw} reached ProgramEmitter -- PackagePlanner should have rejected this"),
    };

    /// <summary>A Flat File Destination's own IBulkSink&lt;TEntity&gt; is constructed directly
    /// (same "construct directly, don't fight the DI container" pattern a Conditional Split
    /// branch's own transform already uses), reusing FileSourceOptions for the file path the
    /// exact same way a CSV source already does -- a destination is just another named
    /// folder/filename pair, not a fundamentally different configuration shape.</summary>
    private static void EmitFlatFileSinkRegistration(List<string> lines, string entityName, FlatFileFlowSink sink)
    {
        lines.Add($"builder.Services.AddScoped<IBulkSink<{entityName}>>(sp =>");
        lines.Add("{");
        lines.Add("    var fileOptions = sp.GetRequiredService<IOptions<FileSourceOptions>>().Value;");
        lines.Add($"    return new FlatFileBulkSink<{entityName}>(");
        lines.Add($"        fileOptions[\"{sink.FileSourceKey}\"].ResolvedPath,");
        lines.Add($"        {(sink.Overwrite ? "true" : "false")},");
        lines.Add($"        {(sink.HeaderLine is null ? "null" : CSharpStringLiteral(sink.HeaderLine))},");
        lines.Add("        [");
        for (var i = 0; i < sink.Columns.Count; i++)
        {
            var column = sink.Columns[i];
            var comma = i < sink.Columns.Count - 1 ? "," : "";
            var width = column.FixedWidth is { } w ? w.ToString() : "null";
            lines.Add($"            new FlatFileColumnFormat({CSharpStringLiteral(column.PropertyName)}, {width}, {CSharpStringLiteral(column.Delimiter)}){comma}");
        }
        lines.Add("        ],");
        lines.Add($"        sp.GetRequiredService<ILogger<FlatFileBulkSink<{entityName}>>>());");
        lines.Add("});");
        lines.Add("");
    }

    /// <summary>Escapes backslash/quote AND control characters -- a flat file's own
    /// ColumnDelimiterDecoded/HeaderLine can contain a literal CR/LF (SSIS's decoded row
    /// delimiter, not its escape-sequence text), and a raw newline inside a non-verbatim C#
    /// string literal is a compile error (CS1010), not just cosmetically wrong.
    /// Internal, not private -- ForEachLoopEmitter reuses this exact escaping for the string
    /// literal pieces of a ForEach Loop's own per-iteration SQL template, same sharing
    /// precedent as TransformEmitter.CollectSsisFunctions/MapPipelineTypeToSsisType.</summary>
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
