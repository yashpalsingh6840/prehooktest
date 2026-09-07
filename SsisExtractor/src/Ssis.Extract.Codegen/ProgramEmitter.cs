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
        w.Line("using Etl.Core.Notifications;");
        w.Line($"using {request.RootNamespace};");
        w.Line($"using {request.RootNamespace}.Model;");
        w.Line("using Microsoft.Extensions.DependencyInjection;");
        w.Blank();
        w.Line($"const string PackageName = {CSharpStringLiteral(request.PackageName)};");
        w.Blank();
        w.Line("var builder = EtlHost.Create(args, PackageName);");
        w.Line($"builder.Services.AddEtlDbContext<{request.DbContextTypeName}>();");
        w.Line("builder.Services.AddEmailNotifications(builder.Configuration);");
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
