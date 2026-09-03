using Ssis.Runtime.Expressions;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

using Ssis.Extract.Model.Analysis;

namespace Ssis.Extract.Codegen;

/// <summary>One Data Flow Task, in execution order, with the components the other emitters
/// need already located. Exactly one of <see cref="FlatFileSource"/>/<see cref="OleDbSource"/>
/// is populated for a flow PackageGenerator can wire up (both null is possible too -- an
/// unsupported/absent source is a gate PackageGenerator reports, not this planner).
/// <see cref="ConditionalSplit"/> is populated instead of a single destination being the whole
/// story when this flow fans out to N destinations -- <see cref="DestinationComponent"/> still
/// points at the split's own DEFAULT branch's destination in that case (kept non-null so
/// existing single-destination code paths that only look at it stay harmless; PackageGenerator
/// checks <see cref="ConditionalSplit"/> first and branches before ever relying on that for
/// real in the split case).</summary>
public sealed record DataFlowPlan(
    string TaskName,
    PipelineSpec Pipeline,
    PipelineComponentSpec? FlatFileSource,
    PipelineComponentSpec? OleDbSource,
    PipelineComponentSpec? DerivedColumn,
    PipelineComponentSpec? Lookup,
    ConditionalSplitPlan? ConditionalSplit,
    PipelineComponentSpec DestinationComponent,
    PipelineComponentSpec? DataConversion = null,
    MergeJoinPlan? MergeJoin = null,
    /// <summary>Added 2026-08-28 testing this tool against a real third-party portfolio
    /// (SSIS_From_Sandeep's Package_Advanced.dtsx, DFT_ExcelImport) -- a
    /// <c>Microsoft.ExcelSource</c>, kept as its own field rather than folded into
    /// <see cref="OleDbSource"/> since it needs a completely different runtime read path
    /// (ExcelDataReader, not a SQL connection) and so its own dispatch in
    /// <see cref="PackageGenerator.ResolveFlowSource"/>.</summary>
    PipelineComponentSpec? ExcelSource = null,
    /// <summary>Added 2026-08-28 testing this tool against a real third-party portfolio
    /// (SSIS_From_Sandeep's Package_Advanced.dtsx, DFT_FlagCustomers) -- a
    /// <c>Microsoft.OLEDBCommand</c> with no destination component at all; the command itself is
    /// the flow's sink, run once per source row. When this is populated,
    /// <see cref="DestinationComponent"/> is a placeholder pointing at the command component
    /// itself (never actually treated as a destination -- <see cref="PackageGenerator"/>
    /// branches on this field before ever consulting <see cref="DestinationComponent"/> for
    /// real, the same convention <see cref="Lookup"/>'s own doc comment already established).</summary>
    OleDbCommandPlan? OleDbCommand = null,
    /// <summary>Added 2026-08-28 testing this tool against a real third-party portfolio
    /// (SSIS_From_Sandeep's Package_Legacy.dtsx, DFT_FixedWidthImport) -- a
    /// <c>Microsoft.Multicast</c> fanning one row to every branch unconditionally. Populated
    /// instead of <see cref="ConditionalSplit"/> when this flow's fan-out has no routing
    /// decision at all; the two are mutually exclusive in one flow.
    /// <see cref="DestinationComponent"/> points at the LAST branch's destination, same
    /// non-authoritative-placeholder convention <see cref="ConditionalSplit"/> already
    /// established.</summary>
    MulticastPlan? Multicast = null,
    /// <summary>Added 2026-08-30, built speculatively (RBC_Demo_ETL's own real instance,
    /// <c>DFT_LookupAndAggregate\AGG_ByRegion</c>, sits downstream of a Lookup that already
    /// blocks the whole flow regardless -- confirmed twice by re-surveying the portfolio; built
    /// on the user's own explicit request, not to close a real gap). A <c>Microsoft.Aggregate</c>
    /// component sitting between the flow's source and its destination -- when populated, the
    /// destination's own row type is the AGGREGATE's own output shape, not the flow's raw
    /// source row shape, so <see cref="PackageGenerator"/> checks this field before
    /// <see cref="ConditionalSplit"/>/<see cref="MergeJoin"/>/etc., the same "must win first"
    /// convention <see cref="Lookup"/>'s own doc comment already established.</summary>
    AggregatePlan? Aggregate = null,
    /// <summary>Added 2026-09-02 (gap-audit Phase 3.2). Every <c>Microsoft.RowCount</c> in this
    /// flow's own pipeline, resolved to its target variable name -- scoped to the
    /// single-source/single-destination shape only (a Conditional Split/Multicast branch's own
    /// mid-chain RowCount, as opposed to the already-handled discarded-dead-end case, remains
    /// unevidenced and unsupported). A RowCount is a pure synchronous passthrough (see
    /// <c>RowCountPayload</c>'s own doc comment), so it needs no chain-walking or column
    /// resolution here at all -- <see cref="PackageGenerator"/> wraps the flow's row source in
    /// one <c>CountingRowSource&lt;TRow&gt;</c> per entry, reproducing the side effect a plain
    /// passthrough-column resolution would otherwise silently drop.</summary>
    IReadOnlyList<RowCountPlan>? RowCounts = null,
    /// <summary>Added 2026-09-02 (gap-audit Phase 3.5). A standalone <c>Microsoft.Sort</c> in the
    /// plain single-source/single-destination flow shape only (a Conditional Split/Multicast
    /// branch's own Sort, and a Sort feeding a Merge Join, are each already handled separately --
    /// <see cref="PackagePlanner.ResolveBranch"/>'s own pass-through widening and
    /// <see cref="PackagePlanner.PlanMergeJoin"/> respectively). Before this field existed, a
    /// standalone Sort was silently INVISIBLE to <see cref="PackagePlanner.PlanDataFlow"/> --
    /// confirmed real, not assumed, by generating a fixture with this exact shape before the fix
    /// and reading the emitted Program.cs: a plain unconditional read, no ordering applied at
    /// all, Sort's own reordering effect silently dropped. Populated only when the Sort's own
    /// single ascending key resolves AND its output provably reaches this flow's own resolved
    /// <see cref="DestinationComponent"/> -- either condition failing is a generation gap, never
    /// a silent no-op (repeating that would be the exact bug this field exists to close).</summary>
    SortKeyPlan? SortKey = null,
    /// <summary>Added 2026-09-02 (gap-audit Phase 3.6). A genuinely multi-independent-source
    /// <c>Microsoft.Merge</c>/<c>Microsoft.UnionAll</c> -- see <see cref="UnionPlan"/>'s own doc
    /// comment for exactly how this is told apart from the already-closed "diverge-then-
    /// reconverge from one shared source" UnionAll shape. When populated,
    /// <see cref="FlatFileSource"/>/<see cref="OleDbSource"/>/<see cref="DerivedColumn"/> are all
    /// null -- the flow's own row source is built entirely from <see cref="UnionSideSource"/>
    /// instead, mirroring <see cref="MergeJoin"/>'s own convention.</summary>
    UnionPlan? Union = null);

/// <summary>One <c>Microsoft.RowCount</c> resolved for <see cref="DataFlowPlan.RowCounts"/> --
/// see <see cref="Ssis.Extract.Model.Pipeline.RowCountPayload"/>'s own doc comment for what's
/// evidenced and what's measured.</summary>
public sealed record RowCountPlan(PipelineComponentSpec Component, string VariableName);

/// <summary>A standalone Sort's own single ascending key, resolved for <see cref="DataFlowPlan.SortKey"/>
/// -- <see cref="KeyColumnName"/> names a buffer column already reachable by plain name (Sort
/// never renames/retypes a column, confirmed real since the 2026-08-28 Conditional-Split/Sort/
/// Merge round), <see cref="KeyClrType"/> is its resolved CLR type (needed for the generated
/// <c>SortingRowSource&lt;TRow,TKey&gt;</c>'s own type argument). Only a single-key, ascending
/// sort is supported -- multi-key or descending is a separate, currently-unevidenced probe, per
/// this round's own stated scope (matching <c>SortKeySpec</c>'s own doc comment: a negative
/// <c>Position</c>, which SSIS's docs suggest means descending, has never been independently
/// confirmed and is deliberately not decoded).</summary>
public sealed record SortKeyPlan(PipelineComponentSpec Sort, string KeyColumnName, SsisPipelineType KeyClrType);

/// <summary>A resolved <c>Microsoft.OLEDBCommand</c> flow -- <see cref="SqlTemplate"/> has every
/// <c>?</c> placeholder rewritten to EF Core's own "{0}", "{1}", ... raw-SQL parameter syntax,
/// positionally matching <see cref="ParameterColumnNames"/>. Resolved 2026-09-02 (gap-audit
/// Phase 3.1) from each bound external column's own INDEX within the input's
/// <c>&lt;externalMetadataColumns&gt;</c> list -- NOT from <c>&lt;inputColumns&gt;</c>
/// declaration order, and NOT from parsing a <c>Param_N</c> numeric suffix (a real EXEC-
/// stored-procedure call names its external columns after the procedure's own parameters, e.g.
/// <c>"@CustomerID"</c>, with no embedded number at all) -- see
/// <see cref="OleDbCommandPayload"/>'s own doc comment for the two live probes this was
/// corrected against. A column with no resolvable external-column binding, or a non-empty
/// <c>ParameterMapping</c> property (never observed populated, but checked defensively), is a
/// named gap in <see cref="PackagePlanner"/>, never guessed.</summary>
public sealed record OleDbCommandPlan(
    PipelineComponentSpec Component,
    string SqlTemplate,
    IReadOnlyList<string> ParameterColumnNames);

/// <summary>One non-GroupBy output column of an Aggregate, resolved back to the real SOURCE
/// column it aggregates (via <c>Ssis.Extract.Dtsx.LineageBuilder</c>'s "Aggregate" edge kind) --
/// e.g. <c>CustomerCount</c> counting non-NULL values of the source's own <c>CustomerID</c>
/// column. <see cref="SourceColumnName"/> is null exactly for a raw AggregationType 2 (CountAll)
/// column with no <c>AggregationColumnId</c> at all -- confirmed real via a live object-model
/// probe (gap-audit Phase 3.3, 2026-09-02): CountAll is the one function SSIS accepts with no
/// column reference whatsoever, a genuine <c>COUNT(*)</c>. Every other raw type requires one.
/// <see cref="AggregationTypeRaw"/> is carried through (not resolved to an enum -- there is no
/// managed CLR enum for this native COM property, confirmed by reflection, see
/// <c>AggregatePayload</c>'s own doc comment) so <see cref="Ssis.Extract.Codegen.ProgramEmitter"/>
/// knows which LINQ aggregation to emit.</summary>
public sealed record AggregateFunctionSpec(string OutputColumnName, string? SourceColumnName, int AggregationTypeRaw);

/// <summary>A resolved <c>Microsoft.Aggregate</c> flow, built speculatively 2026-08-30, widened
/// 2026-09-02 (gap-audit Phase 3.3) from Count-only to every measured AggregationType -- see
/// <c>Ssis.Extract.Model.Pipeline.AggregatePayload</c>'s own doc comment for the full raw-value
/// mapping and how each was measured. Scoped deliberately to exactly the evidenced/measured
/// shape: one GroupBy column (<see cref="GroupByOutputColumnName"/>, resolved back to its own
/// real source column via lineage) plus one or more function columns (Count/CountAll/
/// CountDistinct/Sum/Average/Minimum/Maximum). Any other AggregationType, zero or more than one
/// GroupBy column, or an unresolvable source reference (for anything other than a column-less
/// CountAll) is a generation gap, not a guess -- see <see cref="PackagePlanner"/>'s own
/// <c>PlanAggregate</c>.</summary>
public sealed record AggregatePlan(
    PipelineComponentSpec Component,
    string GroupByOutputColumnName,
    string GroupBySourceColumnName,
    List<AggregateFunctionSpec> Functions);

/// <summary>One side (Left or Right) of a Merge Join, resolved back through its own Sort and
/// (if present) Data Conversion to the true source component -- mirrors
/// <see cref="ConditionalSplitBranchPlan"/>'s own "resolve by walking forward through known
/// pass-through component types" philosophy, just walking BACKWARD from a two-source flow's own
/// join instead of forward from a split.</summary>
public sealed record MergeJoinSideSource(
    PipelineComponentSpec SourceComponent,
    PipelineComponentSpec Sort,
    PipelineComponentSpec? DataConversion);

/// <summary>A <c>Microsoft.MergeJoin</c> flow: two independent source chains (each
/// [Flat File Source|OLE DB Source] -&gt; [Data Conversion] -&gt; Sort, confirmed real from
/// RBC_Demo_ETL's own DFT_SortAndMergeJoin), merged by <see cref="Component"/> straight into
/// one destination (<see cref="DataFlowPlan.DestinationComponent"/>) -- no Derived
/// Column/Conditional Split after the join is evidenced or supported yet.</summary>
public sealed record MergeJoinPlan(
    PipelineComponentSpec Component,
    MergeJoinSideSource Left,
    MergeJoinSideSource Right,
    string JoinType);

/// <summary>One side (input) of a genuinely multi-independent-source <c>Microsoft.Merge</c>/
/// <c>Microsoft.UnionAll</c>, resolved back through an optional Sort and optional Data
/// Conversion to the true source component -- gap-audit Phase 3.6 (2026-09-02), the same
/// backward-walk shape as <see cref="MergeJoinSideSource"/>, generalized: <see cref="Sort"/> is
/// non-null only for a <c>Microsoft.Merge</c> side (SSIS requires sorted input there; a
/// <c>Microsoft.UnionAll</c> side needs no Sort at all, confirmed via the component's own
/// official "combines... without sorting" description text) and carries the resolved single
/// ascending key <c>Microsoft.Merge</c> needs to interleave correctly.</summary>
public sealed record UnionSideSource(
    PipelineComponentSpec SourceComponent,
    PipelineComponentSpec? Sort,
    PipelineComponentSpec? DataConversion,
    SortKeyPlan? SortKey,
    IReadOnlyDictionary<string, string> ColumnAliases);

/// <summary>A genuinely multi-independent-source <c>Microsoft.Merge</c> or <c>Microsoft.UnionAll</c>
/// flow -- gap-audit Phase 3.6 (2026-09-02), distinct from the "diverge-then-reconverge from one
/// shared upstream source" UnionAll shape <see cref="PackagePlanner.ResolveBranch"/>'s own
/// pass-through walk already closed (2026-08-27/28) -- that shape always has a Conditional
/// Split/Multicast upstream feeding this component; this one never does (see
/// <see cref="PackagePlanner.PlanDataFlow"/>'s own gating comment for exactly how the two are
/// told apart). <see cref="IsSortedInterleave"/> is true for <c>Microsoft.Merge</c> (a REAL
/// dtexec-confirmed true sort-preserving interleave, not concatenation -- see
/// <c>Etl.Core.Data.MergeInterleaveRowSource{TRow,TKey}</c>'s own doc comment for the exact
/// probe) and false for <c>Microsoft.UnionAll</c> (plain concatenation -- NOT independently
/// dtexec-confirmed, a real left-unresolved object-model construction quirk, see
/// <c>Etl.Core.Data.ConcatenatingRowSource{TRow}</c>'s own doc comment). Scoped deliberately to
/// the one evidenced/measured shape: every side directly SQL-sourced (OLE DB/ADO NET), no Data
/// Conversion on any side, matching the exact probe fixtures this round was built and verified
/// against -- a Flat File/Excel-sourced side, or a Data-Conversion-fed side, is a named gap for a
/// future round, not guessed at.</summary>
public sealed record UnionPlan(
    PipelineComponentSpec Component,
    bool IsSortedInterleave,
    IReadOnlyList<UnionSideSource> Sides);

/// <summary>One non-default, non-error branch of a Conditional Split resolves to a
/// <see cref="FriendlyExpression"/> (case) or null (the default, no condition of its own) plus
/// the OLE DB Destination this branch's own rows eventually reach -- <see cref="Destination"/>
/// is resolved by walking the pipeline's own &lt;paths&gt; forward from the output's RefId
/// (the same evidenced StartId/EndId pattern RulesEngine.cs already uses for wiring checks),
/// through any chain of Derived Column and/or Union All components, until an OLE DB
/// Destination is reached. <see cref="DerivedColumns"/> is every Derived Column encountered
/// along that walk, in order -- e.g. a per-branch "tag" transform applied after the split but
/// before a Union All remerges two or more branches back to one shared destination. Empty when
/// the branch connects straight to its destination (the original, still-most-common shape).
/// Two or more branches CAN resolve to the same <see cref="Destination"/> instance (a Union
/// All remerge) -- PackageGenerator, not this planner, is what dedupes the resulting
/// entity/table emission and disambiguates the per-branch transform class names.
///
/// <see cref="Destination"/> is null exactly when <see cref="Discarded"/> is true -- added
/// 2026-09-02 for a Multicast branch (only; ResolveBranch gates this on componentKind ==
/// "Multicast", so a Conditional Split branch can never resolve this way) that dead-ends at a
/// Microsoft.RowCount with no path of its own: RBC_Demo_ETL's own DFT_LookupAndAggregate fans
/// one live branch (through an Aggregate) alongside a second branch that only counts rows into
/// a package variable nothing else in the package ever reads. A discarded branch generates no
/// code at all -- ResolveBranch already recorded a non-blocking advisory naming the variable
/// being dropped, so this is never silent.</summary>
public sealed record ConditionalSplitBranchPlan(string OutputName, string? FriendlyExpression, List<PipelineComponentSpec> DerivedColumns, PipelineComponentSpec? Destination, bool Discarded = false);

/// <summary>One Conditional Split, fully resolved: every case in EvaluationOrder, THEN the
/// default branch last (<see cref="Branches"/>[^1]) -- this ordering is what RouterEmitter and
/// ProgramEmitter both rely on to build the if/else-if chain and the branch-index list without
/// re-deriving which entry is the default.</summary>
public sealed record ConditionalSplitPlan(PipelineComponentSpec Component, List<ConditionalSplitBranchPlan> Branches);

/// <summary>One Multicast, fully resolved: every output is an unconditional branch (no
/// evaluation order, no default -- every row goes to every branch). Reuses
/// <see cref="ConditionalSplitBranchPlan"/> for the branch shape (destination + any per-branch
/// Derived Column/Union-All/Sort/Merge/Aggregate chain, via the same <c>ResolveBranch</c>
/// walker) with <c>FriendlyExpression</c> always null -- there is no condition to translate, so
/// <see cref="PackageGenerator"/>'s Multicast emission never calls RouterEmitter at all.
///
/// A branch may also be <see cref="ConditionalSplitBranchPlan.Discarded"/> (a dead-end
/// RowCount) -- <see cref="PackageGenerator.GenerateMulticastFlow"/> skips those; whichever
/// Lookup/Aggregate composition consumes this Multicast directly (see
/// <see cref="DataFlowPlan.Lookup"/>'s own doc comment) is what actually generates the one real
/// evidenced shape (a live branch through an Aggregate).</summary>
public sealed record MulticastPlan(PipelineComponentSpec Component, List<ConditionalSplitBranchPlan> Branches);

/// <summary>One resolved File System Task action -- <see cref="Operation"/> is one of the
/// <c>Etl.Core.Abstractions.FileSystemOperation</c> enum names this generator supports
/// (Copy/Move/Delete/Rename/CreateDirectory), never the raw SSIS enum name verbatim.
/// <see cref="SourcePath"/>/<see cref="DestinationPath"/> are already-resolved literal paths --
/// a FILE connection manager's own <c>ConnectionString</c> when the task referenced one (the
/// evidenced RBC_Demo_ETL shape), or the task's own raw path text otherwise. A variable-driven
/// source/destination is a gate <see cref="PackagePlanner"/> reports, not something this record
/// represents -- there is no runtime-config mapping for an SSIS variable to resolve against.</summary>
public sealed record FileSystemActionPlan(string Operation, string SourcePath, string? DestinationPath, bool Overwrite);

/// <summary>One <c>STOCK:FOREACHLOOP</c> container using a <c>Microsoft.ForEachFileEnumerator</c>,
/// whose body is a single Execute SQL Task -- the real evidenced shape (RBC_Demo_ETL's own
/// Package_Advanced.dtsx FEL_SampleFiles) -- entirely driven by a PropertyExpression referencing
/// the loop's own mapped variable (confirmed real: <c>"..." + @[User::CurrentFile] + "..."</c>).
/// See <see cref="ForEachFileDataFlowPlan"/> for the OTHER supported body shape (a Data Flow
/// Task, built speculatively). A File System Task body, more than one child, or a nested
/// container is still a generation gap, not something either record can represent.
/// <see cref="SqlTemplate"/>'s own C# translation happens in
/// <see cref="ForEachLoopEmitter"/>, not here -- this record only carries the resolved, still-SSIS
/// facts (folder/mask/variable/raw expression text), the same "planner resolves facts, emitter
/// produces code" split every other plan record in this file already follows.</summary>
public sealed record ForEachFileLoopPlan(
    string TaskName,
    string Folder,
    string FileSpec,
    bool Recurse,
    int? NameRetrievalTypeRaw,
    string VariableName,
    string InnerTaskName,
    string SqlTemplate);

/// <summary>One <c>STOCK:FOREACHLOOP</c> container whose body is a Data Flow Task, re-run once
/// per enumerated file -- built speculatively 2026-08-30 (see CLAUDE.md's "ForEach Loop over a
/// Data Flow Task" section for the sign-off): zero real evidenced package anywhere in the
/// tracked portfolio has this shape (the one real ForEach Loop, RBC_Demo_ETL's own
/// FEL_SampleFiles, uses the single-Execute-SQL-Task body <see cref="ForEachFileLoopPlan"/>
/// already models). <see cref="Flow"/> is resolved via the SAME <c>PlanDataFlow</c> every
/// ordinary Data Flow Task goes through -- a per-iteration Data Flow Task is structurally
/// identical to an ordinary one (Derived Column, destination, etc. all resolve the same way);
/// only its own SOURCE FILE PATH varies per iteration, which is why
/// <see cref="FilePathExpression"/> (the source's own Flat File connection manager's
/// <c>ConnectionString</c> PropertyExpression, raw SSIS text -- translated to C# in
/// <c>PackageGenerator</c>, reusing <see cref="ForEachLoopEmitter"/> verbatim, since a
/// connection-manager ConnectionString expression and an Execute SQL Task's SqlStatementSource
/// expression are both just "build a string from <c>@[Namespace::Variable]</c> references", the
/// identical AST shape) is threaded separately rather than folded into <see cref="Flow"/>
/// itself. Scoped deliberately narrow: only a plain single-destination SQL flow sourced from a
/// Flat File Source is supported -- Lookup/Conditional Split/Merge Join/Multicast/OLE DB
/// Command/Excel/ADO NET/Flat File Destination inside a loop body are each a generation gap,
/// never attempted, since combining any of them with this already-speculative feature would
/// stack one unevidenced shape on top of another.</summary>
public sealed record ForEachFileDataFlowPlan(
    string TaskName,
    string Folder,
    string FileSpec,
    bool Recurse,
    int? NameRetrievalTypeRaw,
    string VariableName,
    DataFlowPlan Flow,
    string FilePathExpression);

/// <summary>One step after pre-load: a Data Flow Task, an Execute SQL Task, a File System
/// Task, or a ForEach File Loop that runs AFTER at least one Data Flow Task has already run (a
/// pre-load SQL/File System one never appears here -- it only ever lands in
/// <see cref="PackagePlan.PreLoadStatements"/>/<see cref="PackagePlan.PreLoadFileActions"/>).
/// A ForEach Loop is ALWAYS a Steps entry, regardless of its own position relative to a Data
/// Flow Task -- unlike a one-shot SQL/File System task, a repeating loop has no one-shot
/// "pre-load" equivalent list to be hoisted into, the same reasoning <see cref="FlowStep"/>
/// itself is never hoisted either.</summary>
public abstract record PackageStep
{
    /// <summary>Non-null when a conditional precedence constraint gates this step -- see
    /// <see cref="PackagePlanner.ResolveConditionalConstraints"/>. An init property on the base
    /// record rather than a wrapper record or a per-kind field: a guard is orthogonal to what the
    /// step does (SSIS puts the condition on the EDGE, not the task), and every existing
    /// construction site stays unchanged because it defaults to null.</summary>
    public StepGuard? Guard { get; init; }
}

/// <summary>
/// A translated conditional precedence constraint: the raw SSIS expression (carried so generated
/// code can log what it evaluated) and the C# predicate body over a <c>PackageVariables</c>
/// parameter named <c>v</c>, plus the design-time defaults of every variable it reads.
/// </summary>
public sealed record StepGuard(
    string SsisExpression, string CSharpPredicate, IReadOnlyList<PackageVariableSeed> Seeds);

/// <summary>One package variable's design-time default, so generated code starts from the same
/// value SSIS would have before anything assigns it. Without this a guard reading a variable no
/// ported Script Task has set yet would silently get <c>default(T)</c> instead of the value
/// declared in the .dtsx.</summary>
public sealed record PackageVariableSeed(string SsisName, string ClrTypeName, string CSharpLiteral);

/// <summary>What ResolveConditionalConstraints found: per-step guards, and Failure-constraint
/// successors resolved into failure handlers. Both keyed by the target executable-s refId.</summary>
internal sealed record ConditionalConstraints(
    Dictionary<string, StepGuard> Guards,
    Dictionary<string, FailureHandlerPlan> FailureHandlers);

/// <summary>One resolved Failure precedence constraint: the SQL that runs after the package
/// transaction is rolled back. <paramref name="RefId"/> is carried so the walk can EXCLUDE this
/// executable from the ordinary step list -- generating it in both places would run it on every
/// successful run too.</summary>
public sealed record FailureHandlerPlan(string TaskName, string Sql, string RefId);

public sealed record FlowStep(DataFlowPlan Flow) : PackageStep;

/// <summary><paramref name="ConnectionManagerName"/> is the SSIS connection manager this task's
/// own SqlStatementSource actually runs against (<c>ExecuteSqlTaskPayload.ConnectionName</c>,
/// resolved from its DTSID reference) -- carried through unconditionally, whether or not it
/// turns out to be the package's primary one. Deciding that, and so whether this becomes an
/// ordinary <c>ProgramSqlStep</c> (runs inside the shared package transaction) or a
/// <c>ProgramSecondaryConnectionSqlStep</c> (its own independent, autocommitted connection --
/// see that type's own doc comment for why), needs the package's own resolved target
/// server/database, which <see cref="PackagePlanner"/> does not know at plan time -- that
/// happens in <c>PackageGenerator</c>, after every flow's own destination has been resolved.
/// Null when the raw DTSID reference didn't resolve to a known connection manager at all (a
/// dangling reference) -- unchanged from before this field existed: such a task is still
/// generated as an ordinary <c>ProgramSqlStep</c>, since there is no evidence to say
/// otherwise.</summary>
public sealed record SqlStep(string TaskName, string Sql, string? ConnectionManagerName = null) : PackageStep;
public sealed record FileSystemStep(string TaskName, FileSystemActionPlan Action) : PackageStep;
public sealed record ForEachFileLoopStep(ForEachFileLoopPlan Loop) : PackageStep;
public sealed record ForEachDataFlowLoopStep(ForEachFileDataFlowPlan Loop) : PackageStep;
public sealed record ScriptTaskStep(ExecutableSpec Task) : PackageStep;

/// <summary><see cref="Flows"/> is kept alongside <see cref="Steps"/>, duplicating every
/// FlowStep's own DataFlowPlan, purely so callers that only ever cared about "all the flows,
/// in order" (most existing tests and emitters) don't need to migrate to pattern-matching
/// Steps.</summary>
public sealed record PackagePlan(
    List<string> PreLoadStatements,
    List<FileSystemActionPlan> PreLoadFileActions,
    List<DataFlowPlan> Flows,
    List<PackageStep> Steps,
    List<GenerationGap> Gaps)
{
    /// <summary>Design-time defaults of every package variable a step guard reads, so generated
    /// code starts from the same value SSIS would have before anything assigns it. Deduplicated
    /// and ordinal-sorted, so repeated builds emit identical text.</summary>
    public IReadOnlyList<PackageVariableSeed> VariableSeeds { get; init; } = [];

    /// <summary>Failure-precedence-constraint successors, ordinal-sorted by refId so repeated
    /// builds emit identical text. These are deliberately NOT in <see cref="Steps"/>: they run only
    /// after the package transaction is rolled back.</summary>
    public IReadOnlyList<FailureHandlerPlan> FailureHandlers { get; init; } = [];
}

/// <summary>
/// Walks one package's whole control-flow tree (<c>package.Executables</c>/<c>package.Dag</c>,
/// recursing into any Sequence Container's own <c>Children</c>/<c>Dag</c>) into the ordered
/// facts <see cref="ProgramEmitter"/> and friends need: pre-load SQL text, each Data Flow
/// Task's key components, and any Execute SQL Task that runs AFTER a Data Flow Task (an
/// <see cref="SqlStep"/>, in <see cref="PackagePlan.Steps"/> at its real position -- unlike a
/// pre-load statement, this is not hoisted to the front), in the exact order SSIS would run
/// them.
///
/// Pre-load vs. post-flow is decided from each container's own <c>PrecedenceConstraints</c>
/// (direct predecessor lookup), NOT from a single global "have we seen any flow yet" flag --
/// that simpler design was tried first and rejected after it produced a wrong answer on this
/// tool's own SyntheticParallelShapes.dtsx fixture: SQL_CreateLoadATemp has no precedence
/// relationship at all to the package's other two (fully independent) top-level branches, but
/// a global flag still misclassified it as post-flow purely because DFT_DirectCopy/
/// DFT_ErrorRouting happened to sort earlier in the merged topological order. An Execute SQL
/// Task with no predecessor in its own container inherits whether the CONTAINER itself is
/// already running after a flow (relevant once a Sequence Container is itself downstream of
/// an outer flow); one with predecessors is post-flow iff any direct predecessor is a Data
/// Flow Task or is itself already post-flow -- computed in a single forward pass since
/// <c>dag.TopologicalOrder</c> already guarantees every predecessor is visited first. A
/// container's own "did a flow happen anywhere inside me" result is returned to its caller so
/// a Sequence Container correctly propagates to ITS OWN siblings in the parent container, the
/// same way a bare Data Flow Task would.
///
/// A Sequence Container (<c>STOCK:SEQUENCE</c>) is transparent -- its own children are walked
/// in ITS topological order and folded into the same flat lists, at whatever point the
/// container itself falls in its parent's order. It carries no execution semantics of its own
/// beyond grouping and ordering (confirmed via an isolated object-model probe before writing
/// this: a freshly built Sequence Container's own saved XML has no payload, just
/// <c>&lt;DTS:Executables&gt;</c>/<c>&lt;DTS:PrecedenceConstraints&gt;</c>, exactly the shape
/// <c>DtsxPackageReader.ReadContainerBody</c> already reads generically for every container --
/// see the synthetic fixture this was verified against,
/// <c>tests/Ssis.Extract.Tests/Fixtures/SyntheticNestedContainer.dtsx</c>).
///
/// A ForEach/For Loop container (<c>STOCK:FOREACHLOOP</c>) is NOT walked the same way Sequence
/// is (its own children are never flattened into the parent's flat lists -- unlike Sequence, a
/// loop has real per-iteration semantics that change what "run these children" means, and
/// silently flattening them would generate code that runs each exactly once instead of per
/// iteration, wrong rather than just incomplete). Added 2026-08-28: a
/// <c>Microsoft.ForEachFileEnumerator</c> loop whose own single child is an Execute SQL Task
/// entirely driven by a PropertyExpression referencing the loop's own mapped variable resolves
/// via <see cref="PlanForEachFileLoop"/> into one <see cref="ForEachFileLoopStep"/> instead --
/// <c>Etl.Core</c>'s <c>ForEachLoopStep</c> re-runs that ONE SQL statement per matching file,
/// never "the container's children" generically. Extended 2026-08-30, built speculatively (zero
/// real evidenced package has this shape): the same loop can instead resolve to a
/// <see cref="ForEachDataFlowLoopStep"/> when its single child is a Data Flow Task sourced from
/// a Flat File Source whose own connection manager is expression-driven off the loop's mapped
/// variable -- <c>Etl.Core</c>'s new <c>ForEachFileDataFlowStep&lt;TRow,TEntity&gt;</c> re-runs
/// that whole flow once per file instead. Any other enumerator type, or any other loop body
/// shape (a File System Task, more than one child, a nested container), still reports its own
/// explicit gap -- <c>Etl.Core</c> has no per-iteration re-run capability beyond these two
/// evidenced/speculative shapes.
///
/// An executable type that is none of Sequence/ForEach/Data Flow/Execute SQL, or a Data Flow
/// Task with no OLE DB Destination, is reported as a gap rather than silently skipped.
/// </summary>
public static class PackagePlanner
{
    /// <param name="emitSeams">When true a Script Task becomes a <see cref="ScriptTaskStep"/>
    /// whose logic a human supplies through a compile-enforced seam, instead of the plain
    /// "unsupported executable type" gap. Opt-in for the same reason the flag exists at all:
    /// an unfilled seam deliberately BREAKS the build.</param>
    public static PackagePlan Plan(PackageSpec package, bool emitSeams = false)
    {
        var gaps = new List<GenerationGap>();
        var preLoadStatements = new List<string>();
        var preLoadFileActions = new List<FileSystemActionPlan>();
        var flows = new List<DataFlowPlan>();
        var steps = new List<PackageStep>();

        // Resolved BEFORE the walk so each step can be created already carrying its guard --
        // attaching one afterwards would mean matching steps back to executables, which only
        // FlowStep/ScriptTaskStep even retain enough identity to do.
        var resolved = ResolveConditionalConstraints(package, gaps);
        var guards = resolved.Guards;
        var guardsApplied = new HashSet<string>();

        WalkContainer(package.Executables, package.Dag, package.PrecedenceConstraints, package.ConnectionManagers,
            preLoadStatements, preLoadFileActions, flows, steps, gaps, containerIsPostFlow: false, emitSeams: emitSeams,
            guards: guards, guardsApplied: guardsApplied,
            failureHandlerRefIds: resolved.FailureHandlers.Keys.ToHashSet());

        // Resolved separately from precedence-constraint failure handlers above (an event handler
        // lives in a wholly different part of the model, package.EventHandlers, never inside the
        // ordinary executable tree WalkContainer just walked) but lands in the exact same list --
        // see ResolveErrorEventHandler's own doc comment for why that reuse is correct, not just
        // convenient.
        var errorHandler = ResolveErrorEventHandler(package, gaps, out var claimedHandlerRefIds);

        ReportParallelismFlattening(package, gaps);
        ReportEventHandlers(package, gaps, claimedRefIds: claimedHandlerRefIds);
        ReportUnappliedGuards(package, guards, guardsApplied, gaps);

        var seeds = steps
            .Where(step => step.Guard is not null)
            .SelectMany(step => step.Guard!.Seeds)
            .DistinctBy(seed => seed.SsisName)
            .OrderBy(seed => seed.SsisName, StringComparer.Ordinal)
            .ToList();

        var failureHandlers = resolved.FailureHandlers.Values.ToList();
        if (errorHandler is not null) failureHandlers.Add(errorHandler);

        return new PackagePlan(preLoadStatements, preLoadFileActions, flows, steps, gaps)
        {
            VariableSeeds = seeds,
            FailureHandlers = failureHandlers
                .OrderBy(h => h.RefId, StringComparer.Ordinal)
                .ToList(),
        };
    }

    /// <summary>
    /// A guard that was translated successfully but never reached a step is a silent correctness
    /// hole, so it is reported rather than dropped. The realistic cause is position, not
    /// translation: an Execute SQL or File System Task in PRE-LOAD position is hoisted into a flat
    /// <c>IReadOnlyList&lt;string&gt;</c>/action list that has nowhere to carry a condition, and a
    /// gated executable inside a ForEach loop body or a disabled subtree never becomes a step at
    /// all. Without this check such a package would generate code that runs the gated work
    /// unconditionally while reporting no gap -- exactly the failure class the disabled-executable
    /// and Script-Component-passthrough bugs both belonged to.
    /// </summary>
    private static void ReportUnappliedGuards(
        PackageSpec package, Dictionary<string, StepGuard> guards, HashSet<string> guardsApplied,
        List<GenerationGap> gaps)
    {
        var packageName = package.ObjectName ?? "package";
        foreach (var (refId, guard) in guards.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (guardsApplied.Contains(refId)) continue;
            var to = LeafName(refId);
            gaps.Add(new GenerationGap($"{packageName}.{to}.Constraint",
                $"'{to}' is gated by a conditional precedence constraint (`{guard.SsisExpression}`) that was " +
                "translated successfully, but it does not become a step the condition can be attached to -- it is " +
                "either hoisted into the flat pre-load list (which carries no condition), or inside a loop body or " +
                "skipped subtree. The condition is NOT applied, so this is reported rather than generated.",
                Kind: GapKind.ConditionalConstraint));
        }
    }

    /// <summary>
    /// A precedence constraint can be conditional: it fires only on a specific outcome
    /// (<c>DTS:Value</c> -- Success/Failure/Completion) and/or only when an expression is true
    /// (<c>DTS:EvalOp</c> + <c>DTS:Expression</c>). Found the same way the disabled-executable bug
    /// was: both attributes have always been extracted and read by nothing in this project.
    ///
    /// <para><b>Exactly one of the seven forms is translated, and which one is a measured
    /// conclusion, not a convenience.</b> A real dtexec run of the extractor's own
    /// <c>SyntheticCondConstraint.dtsx</c> probe (one node carrying all seven successor edge kinds,
    /// run twice with the predecessor forced to succeed and then to fail) measured:</para>
    /// <list type="bullet">
    /// <item><c>Value=Success</c> -- ran on success, skipped on failure. The unconditional default.</item>
    /// <item><c>Value=Failure</c> (1) -- skipped on success, RAN on failure.</item>
    /// <item><c>Value=Completion</c> (2) -- ran on both.</item>
    /// <item><c>EvalOp=Expression</c> (1) -- ran whenever the expression was true, on BOTH paths:
    ///   the outcome constraint really is ignored entirely.</item>
    /// <item><c>EvalOp=ExpressionAndConstraint</c> (3), constraint half Success -- ran only when the
    ///   predecessor succeeded AND the expression was true.</item>
    /// <item><c>EvalOp=ExpressionOrConstraint</c> (4) -- ran when either half held.</item>
    /// <item>and, critically: with the predecessor failing, the package still returned
    ///   <c>DTSER_FAILURE</c> (MaximumErrorCount reached) even though its Failure successor ran,
    ///   and the failing task's own committed work persisted.</item>
    /// </list>
    ///
    /// <para>Only <c>EvalOp=3</c> with the constraint half at Success never asks for work after a
    /// failure, so it is the only form <c>PackageRunner</c>'s single whole-package transaction can
    /// reproduce faithfully -- and it is the real evidenced case (RBC_Demo_ETL's
    /// <c>SCR_NotifyAndLogProgress</c> to <c>DFT_BuildSalesSummary</c>, gated on
    /// <c>@[User::RowsLoaded] &gt; 0</c>). Every other form stays a BLOCKING gap whose reason now
    /// names the measured behaviour rather than a general "not supported".</para>
    ///
    /// <para>Raw enum values are read from the real GAC enums (<c>DTSExecResult</c>,
    /// <c>DTSPrecedenceEvalOp</c>) by reflection rather than guessed -- they use different numbering
    /// bases, so "3" means different things depending on which attribute carries it. Note that
    /// <c>EvalOp=3</c> with the constraint half at Success persists as <c>DTS:EvalOp="3"</c> with NO
    /// <c>DTS:Value</c> at all (Success = 0 being the omitted schema default), confirmed from both
    /// the real package and the probe's own saved XML.</para>
    /// </summary>
    /// <returns>Guards keyed by the refId of the executable each one gates, and failure handlers
    /// keyed the same way -- a handler's refId must be excluded from the ordinary step walk.</returns>
    internal static ConditionalConstraints ResolveConditionalConstraints(
        PackageSpec package, List<GenerationGap> gaps)
    {
        var packageName = package.ObjectName ?? "package";
        var guards = new Dictionary<string, StepGuard>();
        var failureHandlers = new Dictionary<string, FailureHandlerPlan>();
        var variables = BuildGuardVariableTable(package);

        Resolve(package.PrecedenceConstraints);
        foreach (var container in EnumerateContainers(package.Executables))
            Resolve(container.PrecedenceConstraints);

        return new ConditionalConstraints(guards, failureHandlers);

        void Resolve(List<PrecedenceConstraintSpec> constraints)
        {
            foreach (var constraint in constraints)
            {
                // Absent means the schema default, which is the unconditional case for both:
                // DTSExecResult.Success = 0, DTSPrecedenceEvalOp.Constraint = 2.
                var outcomeIsSuccess = constraint.Value is null or "0";
                if (outcomeIsSuccess && constraint.EvalOp is null or "2") continue;

                var from = LeafName(constraint.From);
                var to = LeafName(constraint.To);
                var location = $"{packageName}.{from}-{to}.Constraint";

                // Value=Failure with no expression half: the ONE outcome-based form that is
                // generated, as a failure handler run after the rollback. ResolveFailureHandler
                // reports its own gap for any shape it will not take.
                if (constraint.Value == "1" && constraint.EvalOp is null or "2")
                {
                    var handler = ResolveFailureHandler(
                        package, constraint, from, to, location, failureHandlers, gaps);
                    if (handler is not null) failureHandlers[constraint.To] = handler;
                    continue;
                }

                if (constraint.EvalOp != "3" || !outcomeIsSuccess)
                {
                    gaps.Add(new GenerationGap(location, UnsupportedFormReason(from, to, constraint),
                        Kind: GapKind.ConditionalConstraint));
                    continue;
                }

                // Two conditional edges into one executable would need DTS:LogicalAnd's own AND/OR
                // combination semantics, which nothing evidenced exercises and which is not guessed at.
                if (guards.ContainsKey(constraint.To))
                {
                    gaps.Add(new GenerationGap(location,
                        $"'{to}' is gated by more than one conditional precedence constraint; combining them needs " +
                        "DTS:LogicalAnd's own AND/OR semantics, which is not evidenced anywhere and is not guessed at.",
                        Kind: GapKind.ConditionalConstraint));
                    continue;
                }

                var translated = TranslateGuardExpression(constraint.Expression, variables);
                if (translated is GuardOk ok)
                {
                    guards[constraint.To] = new StepGuard(constraint.Expression ?? "", ok.CSharpPredicate, ok.Seeds);
                    continue;
                }

                gaps.Add(new GenerationGap(location,
                    $"the precedence constraint '{from}' to '{to}' fires only when the preceding executable succeeds " +
                    $"AND its expression is true (`{constraint.Expression}`), but that expression " +
                    $"{((GuardNotTranslatable)translated).Reason}.",
                    Kind: GapKind.ConditionalConstraint));
            }
        }
    }

    /// <summary>
    /// A Failure precedence constraint (<c>DTS:Value="1"</c>) resolved into a handler that
    /// <c>PackageRunner</c> runs after the rollback -- see <c>Etl.Core</c>'s
    /// <c>FailureHandlerAction</c>. Keyed out of the ordinary step walk entirely: the same task must
    /// NOT also become a step, or it would run on every successful run too, which is the bug this
    /// whole round exists to fix.
    /// </summary>
    private static FailureHandlerPlan? ResolveFailureHandler(
        PackageSpec package, PrecedenceConstraintSpec constraint, string from, string to, string location,
        Dictionary<string, FailureHandlerPlan> alreadyResolved, List<GenerationGap> gaps)
    {
        void Reject(string why)
        {
            gaps.Add(new GenerationGap(location,
                $"the precedence constraint '{from}' to '{to}' fires only when the preceding executable FAILS " +
                $"(DTSExecResult.Failure), which is generated as a failure handler -- but {why}. Not guessed at.",
                Kind: GapKind.ConditionalConstraint));
        }

        if (alreadyResolved.ContainsKey(constraint.To))
        {
            Reject($"'{to}' is already the target of another Failure constraint");
            return null;
        }

        var executable = EnumerateAllExecutables(package.Executables)
            .FirstOrDefault(e => e.RefId == constraint.To);

        if (executable is null)
        {
            Reject($"'{to}' could not be resolved to an executable in this package");
            return null;
        }

        if (executable.Disabled == true)
        {
            Reject($"'{to}' is disabled, so it would never have run at all");
            return null;
        }

        // The one evidenced handler shape: a single Execute SQL Task (RBC_Demo_ETL's own
        // SQL_LogLoadFailure, whose author even documented it as "Runs only when DFT_LoadCustomers
        // fails"). A Data Flow Task or container here would need a whole second execution position
        // with its own connection and sinks, which nothing real asks for.
        if (executable.ExecuteSqlTask is not { SqlStatementSource: { Length: > 0 } sql })
        {
            Reject($"'{to}' is a {executable.ExecutableType ?? "container"} rather than an Execute SQL Task with " +
                   "a statement; only a single SQL statement is generated in the failure-handler position");
            return null;
        }

        // Every incoming edge must be THIS one. A task also reachable on the success path is not a
        // handler at all -- excluding it from the step walk would silently drop work SSIS runs.
        var allConstraints = AllPrecedenceConstraints(package).ToList();
        var incoming = allConstraints.Where(c => c.To == constraint.To).ToList();
        if (incoming.Count != 1)
        {
            Reject($"'{to}' has {incoming.Count} incoming precedence constraints, so it is also reachable other " +
                   "than on failure");
            return null;
        }

        // And it must be terminal. A successor of a handler would itself only ever run on the
        // failure path, and chaining is not modelled.
        if (allConstraints.Any(c => c.From == constraint.To))
        {
            Reject($"'{to}' has its own successors, and a chain of failure-path work is not modelled");
            return null;
        }

        return new FailureHandlerPlan(executable.ObjectName ?? to, sql, constraint.To);
    }

    /// <summary>
    /// A package-root <c>OnError</c> event handler translated into the SAME
    /// <see cref="FailureHandlerAction"/> position a Failure precedence constraint already uses --
    /// <c>PackageRunner.RunFailureHandlersAsync</c> already runs every entry unconditionally on ANY
    /// exception, which turns out not to be a coincidence: under the precondition checked below,
    /// "the package as a whole fails" and "some task raised OnError" are the SAME event.
    ///
    /// <para><b>Measured via a real dtexec probe first, not assumed</b> (SyntheticEventHandlerProbe,
    /// built specifically to answer this before writing any of this method): OnError fires ONCE PER
    /// FAILING TASK, not once per run -- raising the package's own MaximumErrorCount let two
    /// independent failures both happen and the handler ran twice (2 rows logged). It also
    /// propagates from a child container that declares no handler of its own up to the nearest
    /// ancestor that does (a Sequence Container's own failure, with no handler on the Sequence
    /// itself, fired the PACKAGE-level handler). And reflecting the live object model directly
    /// confirmed <c>MaximumErrorCount</c> defaults to exactly 1 on a Package, a Sequence, AND a
    /// plain Task alike, with <c>FailParentOnFailure</c>/<c>FailPackageOnFailure</c> both defaulting
    /// to false -- so a failing task's own container hits its OWN threshold and becomes Failure,
    /// but that does NOT automatically fail an ancestor (the probe's own package returned
    /// DTSER_SUCCESS despite two real task failures underneath it, precisely because nothing raised
    /// either container's MaximumErrorCount past 1... except in that probe, the PACKAGE's own count
    /// was deliberately raised to 10 to let both branches run, which is exactly the condition that
    /// makes the "runs once" assumption unsafe).</para>
    ///
    /// <para><b>The precondition this rests on:</b> at the schema default of 1, the FIRST failure
    /// anywhere in the tree immediately exhausts its own container's threshold -- and because this
    /// rewrite's own <c>PackageRunner</c> already aborts the WHOLE run the instant any ONE step
    /// throws, "some task failed" and "the whole package failed" collapse into the same event here
    /// too, with no possibility of a second, independent failure ever occurring. So translation is
    /// safe ONLY when every container in the tree (package included) leaves MaximumErrorCount at
    /// that default; anywhere it is raised, a task could fail while the package still overall
    /// succeeds -- a state this rewrite's all-or-nothing step loop cannot represent -- and that is
    /// reported as a gap rather than guessed.</para>
    ///
    /// <para>Scoping to the package root is not a special case under that same precondition: with
    /// every threshold at 1, a handler on some deeper container could only ever fire if nothing
    /// above it also failed on the same run, which is precisely the case the whole-package-abort
    /// model already treats as "the run succeeded" -- so a non-root handler is a genuinely
    /// different, unevidenced shape, not a generalization of this one, and stays a reported gap.</para>
    /// </summary>
    private static FailureHandlerPlan? ResolveErrorEventHandler(
        PackageSpec package, List<GenerationGap> gaps, out HashSet<string> claimedRefIds)
    {
        claimedRefIds = [];
        var packageName = package.ObjectName ?? "package";
        var location = $"{packageName}.EventHandlers[OnError]";

        var live = package.ExecutionSemantics?.DisableEventHandlers == true
            ? []
            : package.EventHandlers
                .Where(h => string.Equals(h.EventName, "OnError", StringComparison.OrdinalIgnoreCase))
                .Where(h => h.Disabled != true)
                .ToList();
        if (live.Count == 0) return null;

        // Claimed the moment a live package-root OnError handler exists to examine, regardless of
        // outcome -- Reject already adds this method's own, more precise gap for the SAME
        // location, so ReportEventHandlers must not also add its generic one right behind it.
        claimedRefIds = [.. live.Select(h => h.RefId)];

        void Reject(string why) => gaps.Add(new GenerationGap(location,
            $"OnError event handler on '{packageName}' would be generated as a failure handler, but {why}. " +
            "Not guessed at.",
            Kind: GapKind.ConditionalConstraint));

        if (live.Count > 1)
        {
            Reject($"'{packageName}' declares {live.Count} live OnError handlers at the package root, and only " +
                   "one is evidenced; combining more than one is not guessed at");
            return null;
        }

        // The precondition: MaximumErrorCount left at the schema default (absent, or ==1)
        // everywhere in the tree -- see doc comment above.
        var badMec = EnumerateAllExecutables(package.Executables)
            .FirstOrDefault(e => e.MaximumErrorCount is not null and not 1);
        if (badMec is not null)
        {
            Reject($"'{badMec.ObjectName ?? badMec.RefId}' sets MaximumErrorCount={badMec.MaximumErrorCount} " +
                   "(the default is 1) -- a task could then fail while the package as a whole still succeeds, " +
                   "which this generator's single whole-package transaction cannot represent");
            return null;
        }
        if (package.ExecutionSemantics?.MaximumErrorCount is not null and not 1)
        {
            Reject($"'{packageName}' itself sets MaximumErrorCount={package.ExecutionSemantics!.MaximumErrorCount} " +
                   "(the default is 1) -- a task could then fail while the package as a whole still succeeds, " +
                   "which this generator's single whole-package transaction cannot represent");
            return null;
        }

        var handler = live[0];
        var liveChildren = handler.Children.Where(c => c.Disabled != true).ToList();
        if (liveChildren.Count == 0) return null; // declares nothing that runs -- silent, matching ReportEventHandlers
        if (liveChildren.Count != 1)
        {
            Reject($"it contains {liveChildren.Count} live executable(s); only a single one is generated");
            return null;
        }

        var executable = liveChildren[0];
        if (executable.ExecuteSqlTask is not { SqlStatementSource: { Length: > 0 } sql })
        {
            Reject($"its one child is a {executable.ExecutableType ?? "container"} rather than an Execute SQL " +
                   "Task with a statement; only a single SQL statement is generated in the failure-handler position");
            return null;
        }

        return new FailureHandlerPlan(executable.ObjectName ?? executable.RefId, sql, handler.RefId);
    }

    /// <summary>Every precedence constraint in the package, at any container depth.</summary>
    private static IEnumerable<PrecedenceConstraintSpec> AllPrecedenceConstraints(PackageSpec package) =>
        package.PrecedenceConstraints.Concat(
            EnumerateContainers(package.Executables).SelectMany(c => c.PrecedenceConstraints));

    /// <summary>Every executable in the tree, at any depth -- a Failure constraint's target may sit
    /// inside a Sequence Container while the constraint itself is declared there too.</summary>
    private static IEnumerable<ExecutableSpec> EnumerateAllExecutables(List<ExecutableSpec> children)
    {
        foreach (var child in children)
        {
            yield return child;
            foreach (var descendant in EnumerateAllExecutables(child.Children))
                yield return descendant;
        }
    }

    /// <summary>The measured reason a form other than "Success AND expression" is not generated.
    /// Deliberately specific per form: every one of these was observed running its successor on the
    /// FAILURE path, which is precisely what a single whole-package transaction cannot express.</summary>
    private static string UnsupportedFormReason(string from, string to, PrecedenceConstraintSpec constraint)
    {
        var head = $"the precedence constraint '{from}' to '{to}' fires ";
        const string Tail =
            " Measured against real SSIS (dtexec, SyntheticCondConstraint.dtsx): the successor runs on BOTH the " +
            "success and the failure path. A pure Failure constraint IS generated (as a failure handler run after " +
            "the rollback), but a form that fires on both paths would need the same task in two positions at once " +
            "-- an ordinary step AND a handler -- which is a separate feature and is not guessed at.";

        return constraint switch
        {
            { Value: "2", EvalOp: null or "2" } =>
                head + "on completion, pass or fail (DTSExecResult.Completion)." + Tail,
            { EvalOp: "1" } =>
                head + $"only when its expression is true (`{constraint.Expression}`), the outcome constraint being " +
                "IGNORED entirely -- so it fires even when the preceding executable failed." + Tail,
            { EvalOp: "4" } =>
                head + $"when the outcome constraint holds OR its expression is true (`{constraint.Expression}`)." + Tail,
            _ =>
                head + $"conditionally on DTS:Value={constraint.Value ?? "(absent)"}/DTS:EvalOp=" +
                $"{constraint.EvalOp ?? "(absent)"} with expression `{constraint.Expression}`." + Tail,
        };
    }

    private abstract record GuardTranslation;
    private sealed record GuardOk(string CSharpPredicate, IReadOnlyList<PackageVariableSeed> Seeds) : GuardTranslation;
    private sealed record GuardNotTranslatable(string Reason) : GuardTranslation;

    /// <summary>
    /// Translates a constraint expression into a C# predicate over a <c>PackageVariables</c>
    /// parameter named <c>v</c>.
    ///
    /// <para><b>Reuses <see cref="ExpressionTranslator.TranslateCondition"/> rather than adding a
    /// third translator</b> -- unlike <see cref="ForEachLoopEmitter"/>, which genuinely could not
    /// (its expression is a STRING-building one with no boolean surface at all). The whole
    /// comparison surface needed here (ordinal <c>==</c>/<c>!=</c> versus culture-aware
    /// <c>&lt;</c>/<c>&gt;</c>, numeric-versus-string operand dispatch, and
    /// <c>&amp;&amp;</c>/<c>||</c>/<c>!</c>) is already oracle-verified against measured SSIS
    /// behaviour, and re-deriving it for variables would be a second copy of semantics that must
    /// not drift. The only difference is what a <c>Reference</c> resolves to: a package VARIABLE
    /// lookup instead of a pipeline buffer column.</para>
    ///
    /// <para>A translation that reaches for an <c>SsisFn.*</c> helper or a null test is refused
    /// rather than emitted. Neither is evidenced in any real constraint expression, and emitting
    /// either would need wiring this round does not do: an <c>SsisFn</c> call needs
    /// <c>SsisFnEmitter</c> to emit that helper and <c>Program.cs</c> to carry the using -- exactly
    /// the latent half-wired hazard that bit <c>RouterEmitter</c> once already -- and a null test
    /// cannot compile against a non-nullable <c>Get&lt;T&gt;</c> result.</para>
    /// </summary>
    private static GuardTranslation TranslateGuardExpression(
        string? expression, IReadOnlyDictionary<string, GuardVariable> variables)
    {
        if (string.IsNullOrWhiteSpace(expression)) return new GuardNotTranslatable("is empty");

        ExprNode root;
        try
        {
            root = Parser.Parse(expression);
        }
        catch (Exception ex)
        {
            return new GuardNotTranslatable($"could not be parsed ({ex.Message})");
        }

        var references = variables.ToDictionary(
            kv => kv.Key,
            kv => new ColumnReference(GuardVariableAccess(kv.Key, kv.Value), kv.Value.Type));

        var translated = ExpressionTranslator.TranslateCondition(root, references);
        if (translated is not TranslatedOk ok)
            return new GuardNotTranslatable(((NotTranslatable)translated).Reason);

        if (ok.CSharpExpression.Contains("SsisFn.") || ok.CSharpExpression.Contains(" is null"))
            return new GuardNotTranslatable(
                "needs an SsisFn helper or a null test, neither of which is wired up for a constraint expression");

        var used = variables
            .Where(kv => ok.CSharpExpression.Contains(GuardVariableAccess(kv.Key, kv.Value), StringComparison.Ordinal))
            .Select(kv => new PackageVariableSeed(kv.Key, kv.Value.ClrTypeName, kv.Value.CSharpLiteral))
            .OrderBy(s => s.SsisName, StringComparer.Ordinal)
            .ToList();

        return new GuardOk(ok.CSharpExpression, used);
    }

    /// <summary>How a guard reads one variable. <c>v</c> is the predicate lambda's own parameter,
    /// named in exactly one place so the emitted lambda and this access can never disagree.</summary>
    private static string GuardVariableAccess(string ssisName, GuardVariable variable) =>
        // GetRequired, not Get: a guard decides whether a step RUNS, so a variable that is
        // missing or holds a different type must fail loudly rather than fall back to default(T)
        // and silently pick a branch -- e.g. a guard reading int against a value some ported
        // Script Task stored as long would have seen 0 and skipped the step, reporting success.
        // See PackageVariables.GetRequired.
        $"v.GetRequired<{variable.ClrTypeName}>(\"{ssisName}\")";

    private sealed record GuardVariable(SsisType Type, string ClrTypeName, string CSharpLiteral);

    /// <summary>
    /// Every package-scoped <c>User::</c> variable a guard could reference, keyed by the qualified
    /// name the expression parser produces for <c>@[User::X]</c>. A variable whose declared variant
    /// type has no unambiguous mapping is simply absent, so a guard referencing it degrades to a
    /// named gap through the ordinary unresolved-reference path rather than a guessed type.
    /// </summary>
    private static Dictionary<string, GuardVariable> BuildGuardVariableTable(PackageSpec package)
    {
        var table = new Dictionary<string, GuardVariable>(StringComparer.Ordinal);
        foreach (var variable in package.Variables)
        {
            if (variable.Namespace != "User") continue;
            var mapped = MapVariantType(variable.DeclaredDataTypeName, variable.Value);
            if (mapped is not null) table[$"{variable.Namespace}::{variable.ObjectName}"] = mapped;
        }
        return table;
    }

    /// <summary>
    /// A variable's declared variant type (<c>DTS:VariableValue/@DataType</c>, already resolved to a
    /// name by <c>SsisTypeCodeMaps.VariantDeclaredTypeName</c>) mapped to the CLR type
    /// <c>PackageVariables.Get&lt;T&gt;</c> is called with, the expression type the shared condition
    /// translator dispatches on, and a C# literal for the design-time default. Deliberately only
    /// the types whose literal form is unambiguous: a DateTime default would need a parse and a
    /// round-trip format decision that nothing evidenced asks for.
    /// </summary>
    private static GuardVariable? MapVariantType(string? declaredTypeName, string? designTimeValue) =>
        declaredTypeName switch
        {
            "Int16" => IntegralGuard(SsisType.I2, "short", designTimeValue),
            "Int32" => IntegralGuard(SsisType.I4, "int", designTimeValue),
            "Int64" => IntegralGuard(SsisType.I8, "long", designTimeValue),
            "String" => new GuardVariable(SsisType.WStr, "string",
                ProgramEmitter.CSharpStringLiteral(designTimeValue ?? "")),
            "Boolean" => new GuardVariable(SsisType.Bool, "bool",
                string.Equals(designTimeValue, "True", StringComparison.OrdinalIgnoreCase) ? "true" : "false"),
            _ => null,
        };

    private static GuardVariable? IntegralGuard(SsisType type, string clrTypeName, string? designTimeValue) =>
        long.TryParse(designTimeValue, out var parsed)
            ? new GuardVariable(type, clrTypeName, clrTypeName == "long" ? $"{parsed}L" : $"{parsed}")
            : null;

    /// <summary>"Package\DFT_LoadCustomers" -> "DFT_LoadCustomers". A constraint's From/To are
    /// package-qualified refId paths; only the leaf reads usefully in a gap message.</summary>
    private static string LeafName(string refIdPath)
    {
        var slash = refIdPath.LastIndexOf('\\');
        return slash >= 0 && slash < refIdPath.Length - 1 ? refIdPath[(slash + 1)..] : refIdPath;
    }

    /// <summary>
    /// Reports the one behavioural difference between SSIS's Control Flow and the generated
    /// <c>PackageRunner</c> that nothing else in this tool surfaces: SSIS runs executables with no
    /// ordering constraint between them CONCURRENTLY (up to <c>MaxConcurrentExecutables</c>), while
    /// <c>PackageRunner</c> runs every step strictly in sequence. The generated code produces the
    /// same DATA -- a topological order always respects every precedence constraint -- so this is
    /// non-blocking (an advisory gap, like the Notification one), not a correctness failure. It is
    /// reported because staying silent about it is the actual defect: a package whose four branches
    /// SSIS ran in parallel generates code that looks complete and takes four times as long.
    ///
    /// Detection is <c>ParallelLevels.Any(l => l.Count > 1)</c>. <see cref="ControlFlowDagSpec.ParallelLevels"/>
    /// warns it is a sound-but-INCOMPLETE accounting of parallelism, which is true for enumerating
    /// WHICH pairs run concurrently -- but not for the only question asked here, whether ANY pair
    /// does. If every level were a singleton then level 0's single node is the unique root, and each
    /// level n+1 node has a predecessor at level n, so the container is a strict chain with no
    /// concurrency at all. So this check is both sound and complete for existence.
    ///
    /// Why it is not simply fixed: <c>Etl.Core</c>'s <c>IUnitOfWork</c> pins ONE SqlConnection open
    /// for the whole run so a bulk insert can enlist in the same transaction as its TRUNCATE. A
    /// SqlTransaction is bound to a single connection and one connection serializes commands, so
    /// parallel branches would need one unit of work each -- giving up whole-package rollback (which
    /// is closer to what SSIS itself did, auto-committing per task) or escalating to MSDTC.
    /// </summary>
    private static void ReportParallelismFlattening(PackageSpec package, List<GenerationGap> gaps)
    {
        var packageName = package.ObjectName ?? "package";
        var concurrency = package.ExecutionSemantics.MaxConcurrentExecutables switch
        {
            null => "MaxConcurrentExecutables not declared, so SSIS's own default of -1 applies: number of logical processors + 2",
            -1 => "MaxConcurrentExecutables=-1: number of logical processors + 2",
            1 => "MaxConcurrentExecutables=1, so SSIS itself ran these one at a time -- sequential generated code matches it exactly",
            var n => $"MaxConcurrentExecutables={n}",
        };

        Report(package.Dag, package.Executables, packageName, isRoot: true);
        foreach (var container in EnumerateContainers(package.Executables))
            Report(container.Dag, container.Children, container.ObjectName ?? container.RefId, isRoot: false);

        void Report(ControlFlowDagSpec dag, List<ExecutableSpec> children, string containerName, bool isRoot)
        {
            if (dag.HasCycle || dag.ParallelLevels.Count == 0) return;

            // Count only the executables that actually RAN. A wave of {enabled, disabled} is not
            // concurrency, and reporting it as such is a live false positive on a real package
            // (RBC_Demo_ETL's Package_Legacy: SQL_AtomicSwap fans out to DFT_FixedWidthImport and
            // to SQL_LegacyStep_DISABLED, so SSIS never ran two things at once there).
            //
            // Filtering the precomputed levels is safe -- disabling a node does not change the
            // ordering it imposes, so nobody else's depth moves -- but it does give up the
            // completeness argument this method's own comment below relies on: with a disabled
            // node as the sole bridge of a diamond, two enabled siblings can now both fall in
            // singleton levels and go unreported. That trade is deliberate. An advisory that
            // fires when nothing ran concurrently is the kind of noise that teaches people to
            // ignore the gap list; a missed report in that one contrived shape does not.
            var disabled = children.Where(e => e.Disabled == true).Select(e => e.RefId).ToHashSet();
            var levels = dag.ParallelLevels
                .Select(level => level.Count(refId => !disabled.Contains(refId)))
                .ToList();

            var widest = levels.Max();
            if (widest < 2) return;

            var affectedWaves = levels.Count(count => count > 1);
            var location = isRoot ? $"{packageName}.Parallelism" : $"{packageName}.{containerName}.Parallelism";
            var where = isRoot ? "at the root of" : $"inside container '{containerName}' of";

            gaps.Add(new GenerationGap(location,
                $"up to {widest} executables {where} '{packageName}' have no ordering constraint between them " +
                $"({affectedWaves} such wave(s)) and ran concurrently under SSIS ({concurrency}); the generated " +
                "PackageRunner runs every step strictly in sequence. Same data, longer wall clock -- Etl.Core's " +
                "IUnitOfWork pins one SqlConnection for the whole run, so true parallelism needs one unit of work " +
                "per branch and gives up whole-package rollback.",
                IsBlocking: false));
        }
    }

    /// <summary>Every executable that owns children, at any depth -- the containers whose own
    /// <see cref="ExecutableSpec.Dag"/> describes a separate ordering problem from its parent's.</summary>
    /// <summary>
    /// Reports every <c>DTS:EventHandler</c> that actually contains work.
    ///
    /// <para>Event handlers were extracted all along -- <c>PackageSpec.EventHandlers</c> and
    /// <c>ExecutableSpec.EventHandlers</c> -- and <b>nothing in Ssis.Extract.Codegen referenced
    /// them at all</b> (grep-confirmed: zero hits before the 2026-09-02 gap audit). So a package
    /// with an OnError handler generated as though it had none, silently, and still reported as
    /// generatable. Exactly the same bug shape as codegen ignoring
    /// <c>ExecutableSpec.Disabled</c>, and equally live: the third-party
    /// Package_Advanced.dtsx has an OnError handler containing an Execute SQL Task
    /// (<c>SQL_LogOnError</c>), so real SSIS writes a log row on any error and the generated job
    /// does not.</para>
    ///
    /// <para><b>Superseded in part 2026-09-03.</b> This method used to say the OnError-vs-Failure-
    /// handler equivalence had not been measured, and that it was deliberately never translated.
    /// It has now been measured (a real dtexec probe, <c>SyntheticEventHandlerProbe.dtsx</c>) and
    /// the one real evidenced shape -- a package-root OnError handler with a single Execute SQL
    /// Task, under the schema-default MaximumErrorCount everywhere -- IS translated, by
    /// <see cref="ResolveErrorEventHandler"/>, into the exact same <c>FailureHandlerAction</c>
    /// position. This method now only reports what that one does not attempt: a non-root owner, a
    /// non-OnError event, more than one live handler, or (the measured reason a general "any event
    /// handler" translation is NOT safe) a MaximumErrorCount raised anywhere in the tree, which is
    /// what actually lets OnError fire more than once in a single run.</para>
    ///
    /// <para>A handler holding no executables (only the stock <c>Propagate</c> variable, which is
    /// what SSDT creates the moment you click into the handler tab) runs nothing, so it is not
    /// reported at all -- reporting it would be the noise that trains people to ignore gaps. One
    /// that is disabled, or whose owner sets <c>DisableEventHandlers</c>, is reported as a
    /// non-blocking advisory instead: it declares work, but SSIS does not run it either.</para>
    /// </summary>
    private static void ReportEventHandlers(PackageSpec package, List<GenerationGap> gaps, HashSet<string> claimedRefIds)
    {
        var owners = new List<(string Owner, bool HandlersDisabled, List<EventHandlerSpec> Handlers)>
        {
            (package.ObjectName, package.ExecutionSemantics?.DisableEventHandlers == true, package.EventHandlers),
        };
        foreach (var executable in EnumerateContainers(package.Executables))
            owners.Add((executable.ObjectName ?? executable.RefId, false, executable.EventHandlers));

        foreach (var (owner, handlersDisabled, handlers) in owners)
        {
            foreach (var handler in handlers)
            {
                if (claimedRefIds.Contains(handler.RefId)) continue; // ResolveErrorEventHandler already reported (or resolved) this one

                var live = handler.Children.Where(c => c.Disabled != true).ToList();
                if (live.Count == 0) continue; // declares nothing that runs

                var what = string.Join(", ", live.Select(c => $"'{c.ObjectName ?? c.RefId}' ({c.ExecutableType})"));
                var location = $"{owner}.EventHandlers[{handler.EventName}]";

                if (handlersDisabled || handler.Disabled == true)
                {
                    gaps.Add(new GenerationGap(location,
                        $"{handler.EventName} event handler on '{owner}' contains {what}, but it is disabled -- SSIS does not run it either, so generated code omitting it matches. Reported only so the difference between the package and the generated job is never silent.",
                        IsBlocking: false));
                    continue;
                }

                // A live package-root OnError handler is claimed by ResolveErrorEventHandler and
                // never reaches this line (see claimedRefIds above) -- so getting here means this
                // handler is a different, unevidenced shape: a non-root owner, or an event other
                // than OnError. Neither is guessed at.
                gaps.Add(new GenerationGap(location,
                    $"{handler.EventName} event handler on '{owner}' contains {what} -- SSIS runs this when that event fires and generated code does not run it at all. Only a package-root OnError handler with a single Execute SQL Task is translated (see ResolveErrorEventHandler); this is a different, unevidenced shape."));
            }
        }
    }

    private static IEnumerable<ExecutableSpec> EnumerateContainers(List<ExecutableSpec> executables)
    {
        foreach (var executable in executables)
        {
            if (executable.Children.Count == 0) continue;
            // A disabled container ran nothing at all, so neither it nor anything nested inside
            // it describes an ordering problem the generated code has to answer for.
            if (executable.Disabled == true) continue;
            yield return executable;
            foreach (var nested in EnumerateContainers(executable.Children))
                yield return nested;
        }
    }

    /// <summary>
    /// Reports the one ordering hazard Script Task steps introduce. PreLoadStatements and
    /// PreLoadFileActions are HOISTED: IEtlPackage runs all of them before any step, whatever
    /// their real position was. That is harmless while everything pre-flow is itself hoisted --
    /// which was true until a Script Task became a step -- but the moment a step sits in the
    /// pre-flow region, a hoisted statement that the package actually ordered AFTER it silently
    /// runs BEFORE it instead.
    ///
    /// Ordered-after is tested against transitive ancestors, not direct predecessors, so a chain
    /// of any length is caught; and testing ancestry rather than walk position matters, because
    /// two UNORDERED pre-flow tasks may legitimately run in either order (SSIS would have run them
    /// concurrently) and must not be reported. Blocking, unlike this file's other advisories: the
    /// generated code would genuinely do the wrong thing in the wrong order.
    /// </summary>
    private static void ReportHoistingInversion(
        ExecutableSpec hoisted, HashSet<string> hoistedAncestors,
        List<ExecutableSpec> preFlowScriptTasks, string kind, List<GenerationGap> gaps)
    {
        var inverted = preFlowScriptTasks.Where(t => hoistedAncestors.Contains(t.RefId)).ToList();
        if (inverted.Count == 0) return;

        var names = string.Join(", ", inverted.Select(t => $"'{t.ObjectName ?? t.RefId}'"));
        gaps.Add(new GenerationGap($"{hoisted.ObjectName ?? hoisted.RefId}.Hoisting",
            $"{kind} '{hoisted.ObjectName ?? hoisted.RefId}' is ordered AFTER Script Task(s) {names} " +
            "in the package, but pre-load statements/actions are hoisted ahead of every step and a " +
            "Script Task is a step -- so the generated code would run them in the opposite order. " +
            "Move the work into the Script Task's own fill, or put an intervening Data Flow Task " +
            "between them so this becomes an ordinary post-flow step."));
    }

    /// <summary>
    /// One non-blocking advisory per skipped executable. Skipping is CORRECT -- it is what SSIS
    /// itself did -- so this is not a gap in the "we could not translate this" sense and gets no
    /// work packet. It exists because silence about a real difference between the .dtsx and the
    /// generated output is this tool's own recurring failure mode: a reader comparing the two
    /// would otherwise find a task in the package with no counterpart anywhere in the code and
    /// have no way to tell whether it was handled deliberately or dropped by a bug.
    /// </summary>
    private static void ReportDisabledSkip(ExecutableSpec executable, List<GenerationGap> gaps)
    {
        var name = executable.ObjectName ?? executable.RefId;
        var children = executable.Children.Count > 0
            ? $" and its {executable.Children.Count} child executable(s)"
            : "";
        gaps.Add(new GenerationGap($"{name}.Disabled",
            $"'{name}' ({executable.ExecutableType}) is disabled in the package, so it{children} " +
            "generated nothing -- matching SSIS, which skips a disabled executable while still " +
            "running everything ordered after it. Nothing to do; re-enable it in the .dtsx and " +
            "regenerate if it was disabled by accident.",
            IsBlocking: false));
    }

    /// <summary>Returns true if, by the time this container finishes (from its parent's point
    /// of view), a Data Flow Task has run -- either because <paramref name="containerIsPostFlow"/>
    /// was already true coming in, or because one ran somewhere inside.</summary>
    private static bool WalkContainer(
        List<ExecutableSpec> children, ControlFlowDagSpec dag, List<PrecedenceConstraintSpec> constraints,
        List<ConnectionManagerSpec> connectionManagers,
        List<string> preLoadStatements, List<FileSystemActionPlan> preLoadFileActions,
        List<DataFlowPlan> flows, List<PackageStep> steps,
        List<GenerationGap> gaps, bool containerIsPostFlow, bool emitSeams,
        Dictionary<string, StepGuard> guards, HashSet<string> guardsApplied,
        HashSet<string> failureHandlerRefIds)
    {
        var byRefId = children.ToDictionary(e => e.RefId);

        // Attaches the conditional-constraint guard gating this executable, if any, and records
        // that the guard actually reached a step -- ReportUnappliedGuards turns one that never did
        // into a gap rather than letting the gated work run unconditionally in silence.
        PackageStep Gated(string refId, PackageStep step)
        {
            if (!guards.TryGetValue(refId, out var guard)) return step;
            guardsApplied.Add(refId);
            return step with { Guard = guard };
        }

        // dag.TopologicalOrder is empty by design for 0-1 children (that type's own doc
        // comment) -- fall back to declaration order in that case.
        var order = dag.TopologicalOrder.Count > 0
            ? dag.TopologicalOrder
            : children.Select(e => e.RefId).ToList();

        var predecessorsByRefId = new Dictionary<string, List<string>>();
        foreach (var c in constraints)
        {
            if (!byRefId.ContainsKey(c.From) || !byRefId.ContainsKey(c.To)) continue;
            if (!predecessorsByRefId.TryGetValue(c.To, out var list))
                predecessorsByRefId[c.To] = list = [];
            list.Add(c.From);
        }

        // refIds (within THIS container only) known to be a Data Flow Task or to already run
        // after one -- built up as we go, in topological order, so every predecessor is
        // resolved before its successors are evaluated.
        var flowOrAfter = new HashSet<string>();
        var sawDataFlowInThisContainer = false;

        // Transitive ancestors per refId, accumulated in topological order so every
        // predecessor's own set is complete before it is read. Used only by the hoisting guard
        // below -- direct predecessors are not enough there, since the inversion it looks for
        // can be several edges away.
        var ancestors = new Dictionary<string, HashSet<string>>();

        // Script Tasks planned in PRE-FLOW position. A pre-load statement or file action is
        // hoisted ahead of every step, so one of these ordered BEFORE such a statement in the
        // package would have its real order silently inverted -- see ReportHoistingInversion.
        var preFlowScriptTasks = new List<ExecutableSpec>();

        foreach (var refId in order)
        {
            if (!byRefId.TryGetValue(refId, out var executable))
            {
                gaps.Add(new GenerationGap(refId, "topological order references an executable not found in its own container"));
                continue;
            }

            // Computed before the disabled check on purpose: a disabled node still ORDERS its
            // neighbours (measured -- successors run), so it has to stay a vertex here even
            // though nothing is generated for it.
            var ownAncestors = new HashSet<string>();
            if (predecessorsByRefId.TryGetValue(refId, out var ancestorPreds))
            {
                foreach (var pred in ancestorPreds)
                {
                    ownAncestors.Add(pred);
                    if (ancestors.TryGetValue(pred, out var inherited)) ownAncestors.UnionWith(inherited);
                }
            }
            ancestors[refId] = ownAncestors;

            // A disabled executable is skipped outright -- and skipping exactly this node, and
            // NOTHING else, is the faithful translation. That was measured against the real SSIS
            // runtime rather than reasoned about (SyntheticDisabledTask.dtsx under dtexec): the
            // disabled task did not run; a disabled CONTAINER's children did not run either
            // (this one `continue` covers that too, by never reaching the STOCK:SEQUENCE branch
            // below); and its SUCCESSORS DID run -- SSIS treats a disabled node as satisfied for
            // precedence purposes, so the ordering it imposes on everything else is unchanged,
            // which is exactly why nothing downstream needs skipping with it.
            //
            // Deliberately NOT added to flowOrAfter: a disabled Data Flow Task never ran, so
            // nothing ordered after it is "after a flow", and a following Execute SQL Task
            // correctly stays a pre-load statement.
            // A Failure-constraint successor is generated in the failure-handler position instead
            // (PackagePlan.FailureHandlers), so it must NOT also become a step or a pre-load
            // statement -- doing both would run it on every successful run, which is the exact bug
            // this handling exists to fix. Like the disabled skip below, this is deliberately not
            // added to flowOrAfter: it never runs on the success path, so it cannot make anything
            // ordered after it "post-flow".
            if (failureHandlerRefIds.Contains(refId)) continue;

            if (executable.Disabled == true)
            {
                ReportDisabledSkip(executable, gaps);
                continue;
            }

            var isPostFlow = predecessorsByRefId.TryGetValue(refId, out var preds)
                ? preds.Any(flowOrAfter.Contains)
                : containerIsPostFlow;

            if (executable.ExecutableType == "STOCK:SEQUENCE")
            {
                var childRanAfterFlow = WalkContainer(executable.Children, executable.Dag, executable.PrecedenceConstraints, connectionManagers,
                    preLoadStatements, preLoadFileActions, flows, steps, gaps, containerIsPostFlow: isPostFlow, emitSeams: emitSeams,
                    guards: guards, guardsApplied: guardsApplied, failureHandlerRefIds: failureHandlerRefIds);
                if (childRanAfterFlow)
                {
                    flowOrAfter.Add(refId);
                    sawDataFlowInThisContainer = true;
                }
                continue;
            }

            if (executable.ExecutableType == "STOCK:FOREACHLOOP")
            {
                var loopStep = PlanForEachFileLoop(executable, connectionManagers, gaps);
                if (loopStep is not null)
                {
                    steps.Add(Gated(refId, loopStep));
                    flowOrAfter.Add(refId);
                }
                continue;
            }

            if (executable.ExecuteSqlTask is { } sqlTask)
            {
                if (string.IsNullOrWhiteSpace(sqlTask.SqlStatementSource))
                {
                    gaps.Add(new GenerationGap(executable.ObjectName ?? refId, "Execute SQL Task has no SqlStatementSource"));
                    continue;
                }

                if (isPostFlow)
                {
                    steps.Add(Gated(refId, new SqlStep(executable.ObjectName ?? refId, sqlTask.SqlStatementSource, sqlTask.ConnectionName)));
                    flowOrAfter.Add(refId);
                }
                else
                {
                    ReportHoistingInversion(executable, ownAncestors, preFlowScriptTasks, "Execute SQL Task", gaps);
                    preLoadStatements.Add(sqlTask.SqlStatementSource);
                }
                continue;
            }

            if (executable.FileSystemTask is { } fileSystemTask)
            {
                var action = ResolveFileSystemAction(executable.ObjectName ?? refId, fileSystemTask, connectionManagers, gaps);
                if (action is null) continue; // ResolveFileSystemAction already added the gap

                if (isPostFlow)
                {
                    steps.Add(Gated(refId, new FileSystemStep(executable.ObjectName ?? refId, action)));
                    flowOrAfter.Add(refId);
                }
                else
                {
                    ReportHoistingInversion(executable, ownAncestors, preFlowScriptTasks, "File System Task", gaps);
                    preLoadFileActions.Add(action);
                }
                continue;
            }

            if (executable.DataFlowTask is { } dataFlowTask)
            {
                var flow = PlanDataFlow(executable.ObjectName ?? refId, dataFlowTask.Pipeline, gaps);
                if (flow is not null)
                {
                    flows.Add(flow);
                    steps.Add(Gated(refId, new FlowStep(flow)));
                    flowOrAfter.Add(refId);
                    sawDataFlowInThisContainer = true;
                }
                continue;
            }

            // A Script Task always becomes a step at its own TRUE position, with no pre-load/
            // post-flow branching of the kind an Execute SQL Task gets. There is no third
            // "pre-load script" list on IEtlPackage to hoist it into, and inventing one would
            // compound the ordering problem the two existing hoisted lists already have (see the
            // guard right below). Steps preserve order relative to flows and to each other, which
            // is what actually matters.
            if (emitSeams && executable.ScriptTask is not null)
            {
                steps.Add(Gated(refId, new ScriptTaskStep(executable)));

                // A ported Script Task's own SQL runs inside the package transaction, so it is
                // never a "flow" -- but it CAN follow one, and anything after it must still see
                // itself as post-flow. Propagate whatever position it inherited rather than
                // resetting it.
                if (isPostFlow) flowOrAfter.Add(refId);
                else preFlowScriptTasks.Add(executable);
                continue;
            }

            // Recognized explicitly rather than falling through to the generic "unsupported
            // executable type" message below: package-to-package invocation is a genuine,
            // deferred ARCHITECTURAL decision (in-process reference vs. shell-out vs.
            // macro-flatten -- see ExecutePackageTaskPayload's own doc comment), not a
            // translation failure this planner merely hasn't gotten to yet. Stating what the
            // task actually references (mode, child package, connection) up front is what lets a
            // human size that decision without opening the .dtsx.
            if (executable.ExecutePackageTask is { } execPkg)
            {
                var mode = execPkg.UseProjectReference == true ? "project reference" : "file/legacy reference";
                var target = execPkg.PackageName is { Length: > 0 } pn ? $"'{pn}'" : "(no package name recorded)";
                var connectionDetail = execPkg.UseProjectReference != true && execPkg.ConnectionName is { Length: > 0 } cn
                    ? $", via connection manager '{cn}'"
                    : "";
                gaps.Add(new GenerationGap(executable.ObjectName ?? refId,
                    $"Execute Package Task ({mode}) targets {target}{connectionDetail} -- running one generated package from inside another is a deferred architectural decision (in-process reference vs. shell-out vs. macro-flatten), not yet implemented by this tool. The child package is extracted independently; nothing here composes the two.",
                    Kind: GapKind.Unclassified));
                continue;
            }

            // A Script Task is classified rather than left Unclassified: its real source IS present
            // in the .dtsx (ScriptTaskPayload.ProjectItems), so it is a translation job a human
            // working with AI can close, not missing tool support. Every OTHER unsupported
            // executable type stays Unclassified and therefore lands in GapTier.MissingToolSupport.
            var isScriptTask = executable.ScriptTask is not null;
            gaps.Add(new GenerationGap(executable.ObjectName ?? refId,
                $"executable type '{executable.ExecutableType}' is not supported by this planner yet -- only Data Flow Task, Execute SQL Task, and File System Task are",
                Kind: isScriptTask ? GapKind.ScriptTask : GapKind.Unclassified,
                EvidenceRefId: isScriptTask ? executable.RefId : null));
        }

        return containerIsPostFlow || sawDataFlowInThisContainer;
    }

    /// <summary>
    /// Resolves a <c>STOCK:FOREACHLOOP</c> executable to a <see cref="ForEachFileLoopStep"/> or
    /// <see cref="ForEachDataFlowLoopStep"/>, or reports exactly why it can't be generated --
    /// fatal for just this loop, not the whole package, matching every other single-executable
    /// gap in <see cref="WalkContainer"/>. Deliberately narrow: only a
    /// <c>Microsoft.ForEachFileEnumerator</c> whose loop body is EXACTLY one child, either an
    /// Execute SQL Task entirely driven by a PropertyExpression referencing the loop's own
    /// mapped variable (the shape evidenced from RBC_Demo_ETL's own <c>FEL_SampleFiles</c>) or a
    /// Data Flow Task sourced from an expression-driven Flat File Source (built speculatively --
    /// see <see cref="ForEachFileDataFlowPlan"/>'s own doc comment), is supported. See either
    /// plan record's own doc comment for why every other shape is a gap rather than a guess.
    /// </summary>
    private static PackageStep? PlanForEachFileLoop(ExecutableSpec loop, List<ConnectionManagerSpec> connectionManagers, List<GenerationGap> gaps)
    {
        var taskName = loop.ObjectName ?? loop.RefId;
        var payload = loop.ForEachLoop;
        if (payload?.EnumeratorCreationName != "Microsoft.ForEachFileEnumerator" || payload.FileEnumerator is not { } fileEnum)
        {
            gaps.Add(new GenerationGap(taskName,
                $"ForEach Loop uses enumerator '{payload?.EnumeratorCreationName ?? "(unknown)"}' -- only Microsoft.ForEachFileEnumerator is supported"));
            return null;
        }

        if (string.IsNullOrEmpty(fileEnum.Folder) || string.IsNullOrEmpty(fileEnum.FileSpec))
        {
            gaps.Add(new GenerationGap(taskName, "ForEach File Enumerator has no Folder/FileSpec -- not supported"));
            return null;
        }

        var mapping = payload.VariableMappings.FirstOrDefault();
        if (mapping is null)
        {
            gaps.Add(new GenerationGap(taskName, "ForEach Loop has no variable mapping -- not supported"));
            return null;
        }

        if (loop.Children.Count != 1)
        {
            gaps.Add(new GenerationGap(taskName,
                $"ForEach Loop has {loop.Children.Count} child executable(s) -- only a single Execute SQL Task or Data Flow Task body is supported"));
            return null;
        }

        var inner = loop.Children[0];

        // The loop would iterate real files and do nothing on each. Not evidenced in any real
        // package, but it costs one check to be right rather than emit a loop whose body runs a
        // statement SSIS never ran.
        if (inner.Disabled == true)
        {
            ReportDisabledSkip(inner, gaps);
            return null;
        }

        if (inner.DataFlowTask is { } dataFlowTask)
        {
            var dataFlowPlan = PlanForEachDataFlowLoop(taskName, fileEnum, mapping.VariableName, inner, dataFlowTask, connectionManagers, gaps);
            return dataFlowPlan is null ? null : new ForEachDataFlowLoopStep(dataFlowPlan);
        }

        if (inner.ExecuteSqlTask is not { } sqlTask)
        {
            gaps.Add(new GenerationGap(taskName,
                $"ForEach Loop's own body is '{inner.ExecutableType}', not an Execute SQL Task or a Data Flow Task -- not supported"));
            return null;
        }

        var expr = inner.PropertyExpressions.FirstOrDefault(p => p.PropertyName == "SqlStatementSource")?.Expression;
        if (string.IsNullOrEmpty(expr))
        {
            // A static SqlStatementSource with no per-iteration expression would run the exact
            // SAME statement on every iteration -- technically runnable via WalkContainer's own
            // ordinary Execute SQL Task handling, but almost certainly not the intended
            // semantics of putting it inside a loop at all. Reported explicitly rather than
            // silently generating N identical inserts.
            var reason = string.IsNullOrWhiteSpace(sqlTask.SqlStatementSource)
                ? "ForEach Loop's own Execute SQL Task has neither a SqlStatementSource nor a PropertyExpression -- not supported"
                : "ForEach Loop's own Execute SQL Task has a static SqlStatementSource with no per-iteration expression -- every iteration would run identical SQL, which is very likely not intended; not supported";
            gaps.Add(new GenerationGap(taskName, reason));
            return null;
        }

        return new ForEachFileLoopStep(new ForEachFileLoopPlan(taskName, fileEnum.Folder!, fileEnum.FileSpec!, fileEnum.Recurse ?? false,
            fileEnum.FileNameRetrievalTypeRaw, mapping.VariableName, inner.ObjectName ?? inner.RefId, expr));
    }

    /// <summary>Resolves a ForEach Loop's body when it's a Data Flow Task rather than the single
    /// Execute SQL Task shape above -- built speculatively 2026-08-30, see
    /// <see cref="ForEachFileDataFlowPlan"/>'s own doc comment. Reuses <see cref="PlanDataFlow"/>
    /// verbatim for the flow's own internals (Derived Column, destination, etc. all resolve
    /// exactly like an ordinary Data Flow Task); the only new fact resolved here is which
    /// connection manager's own <c>ConnectionString</c> PropertyExpression drives the
    /// per-iteration file path -- the same "must be an expression, not a static default" check
    /// <see cref="PlanForEachFileLoop"/>'s own Execute SQL Task branch already makes for
    /// <c>SqlStatementSource</c>.</summary>
    private static ForEachFileDataFlowPlan? PlanForEachDataFlowLoop(
        string taskName, ForEachFileEnumeratorSpec fileEnum, string variableName,
        ExecutableSpec inner, DataFlowTaskPayload dataFlowTask, List<ConnectionManagerSpec> connectionManagers, List<GenerationGap> gaps)
    {
        var innerTaskName = inner.ObjectName ?? inner.RefId;
        var flow = PlanDataFlow(innerTaskName, dataFlowTask.Pipeline, gaps);
        if (flow is null) return null; // PlanDataFlow already added the reason

        if (flow.FlatFileSource is not { } flatFileSource || flatFileSource.FlatFileSource?.ConnectionName is not { } csvCmName)
        {
            gaps.Add(new GenerationGap(taskName,
                $"{innerTaskName}: ForEach Loop's own Data Flow Task body has no Flat File Source with a resolvable connection manager -- only a per-iteration Flat File Source is supported inside a loop body"));
            return null;
        }

        var connectionManager = connectionManagers.FirstOrDefault(cm => cm.ObjectName == csvCmName);
        var expr = connectionManager?.PropertyExpressions.FirstOrDefault(p => p.PropertyName == "ConnectionString")?.Expression;
        if (string.IsNullOrEmpty(expr))
        {
            gaps.Add(new GenerationGap(taskName,
                $"{innerTaskName}: connection manager '{csvCmName}' has a static ConnectionString with no per-iteration expression -- every iteration would read the identical file, which is very likely not intended; not supported"));
            return null;
        }

        return new ForEachFileDataFlowPlan(taskName, fileEnum.Folder!, fileEnum.FileSpec!, fileEnum.Recurse ?? false,
            fileEnum.FileNameRetrievalTypeRaw, variableName, flow, expr);
    }

    /// <summary>
    /// Resolves one File System Task's operation and paths, or reports why it can't be
    /// generated. <see cref="FileSystemTaskPayload.SourceIsVariable"/>/<c>DestinationIsVariable</c>
    /// is always fatal -- no runtime-config mapping exists for an SSIS variable's value. A path
    /// that resolves to neither a variable nor a connection manager is used as a literal
    /// (matches an object-model-confirmed shape, see FileSystemTaskPayload's own doc comment),
    /// though no real evidenced package uses this form yet.
    /// </summary>
    private static FileSystemActionPlan? ResolveFileSystemAction(
        string taskName, FileSystemTaskPayload payload, List<ConnectionManagerSpec> connectionManagers, List<GenerationGap> gaps)
    {
        var operation = payload.OperationRaw switch
        {
            null => "Copy", // schema default, omitted from the XML when unset
            "CopyFile" => "Copy",
            "MoveFile" => "Move",
            "DeleteFile" => "Delete",
            "RenameFile" => "Rename",
            "CreateDirectory" => "CreateDirectory",
            _ => null,
        };
        if (operation is null)
        {
            gaps.Add(new GenerationGap(taskName,
                $"File System Task operation '{payload.OperationRaw}' is not supported yet -- only Copy/Move/Delete/Rename/CreateDirectory File are"));
            return null;
        }

        var source = ResolveFileSystemPath(taskName, "source", payload.SourcePathRaw, payload.SourceIsVariable, payload.SourceConnectionName, connectionManagers, gaps);
        if (source is null) return null; // ResolveFileSystemPath already added the gap

        string? destination = null;
        if (operation is "Copy" or "Move" or "Rename")
        {
            destination = ResolveFileSystemPath(taskName, "destination", payload.DestinationPathRaw, payload.DestinationIsVariable, payload.DestinationConnectionName, connectionManagers, gaps);
            if (destination is null) return null; // ResolveFileSystemPath already added the gap
        }

        return new FileSystemActionPlan(operation, source, destination, payload.OverwriteDestination ?? false);
    }

    private static string? ResolveFileSystemPath(
        string taskName, string role, string? pathRaw, bool? isVariable, string? connectionName,
        List<ConnectionManagerSpec> connectionManagers, List<GenerationGap> gaps)
    {
        if (isVariable == true)
        {
            gaps.Add(new GenerationGap(taskName,
                $"File System Task's {role} path is driven by a variable ('{pathRaw}') -- not supported yet, no runtime-config mapping for an SSIS variable exists"));
            return null;
        }

        if (connectionName is not null)
        {
            var cm = connectionManagers.FirstOrDefault(c => c.ObjectName == connectionName);
            if (cm?.ConnectionString is { } path) return path;

            gaps.Add(new GenerationGap(taskName,
                $"File System Task's {role} connection manager '{connectionName}' has no design-time default path -- not supported"));
            return null;
        }

        if (string.IsNullOrEmpty(pathRaw))
        {
            gaps.Add(new GenerationGap(taskName, $"File System Task has no {role} path"));
            return null;
        }

        return pathRaw;
    }

    private static DataFlowPlan? PlanDataFlow(string taskName, PipelineSpec pipeline, List<GenerationGap> gaps)
    {
        // Checked before the multi-source gate below -- a Merge Join is EXACTLY the "something
        // merges them" case that gate's own comment names, so it must resolve its own two
        // sources structurally before that gate ever sees (and rejects) them.
        var mergeJoinComponent = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.MergeJoin");
        if (mergeJoinComponent is not null)
            return PlanMergeJoin(taskName, pipeline, mergeJoinComponent, gaps);

        // Same carve-out, for a genuinely multi-independent-source Microsoft.Merge/UnionAll --
        // gap-audit Phase 3.6 (2026-09-02). Gated on there being NO Conditional Split/Multicast
        // anywhere in this pipeline: every already-closed "diverge-then-reconverge from one
        // shared upstream source" UnionAll shape (2026-08-27/28) always has one of those feeding
        // it, and is already fully handled by ResolveBranch's own forward pass-through walk
        // (reached later, via PlanConditionalSplit/Multicast below) -- letting THIS check fire
        // for that shape too would wrongly intercept it: ResolveUnionSide's own backward walk
        // expects a real source/Sort/Data-Conversion feeding each input, not a Conditional
        // Split's own per-branch output, and would report a confusing, wrong gap instead of
        // falling through to the already-working resolution.
        var unionComponent = pipeline.Components.FirstOrDefault(c => c.ComponentClassId is "Microsoft.Merge" or "Microsoft.UnionAll");
        var hasSplitOrMulticastUpstream = pipeline.Components.Any(c => c.ComponentClassId is "Microsoft.ConditionalSplit" or "Microsoft.Multicast");
        if (unionComponent is not null && !hasSplitOrMulticastUpstream)
            return PlanUnion(taskName, pipeline, unionComponent, gaps);

        // More than one source component in a single Data Flow Task means something merges them
        // (Merge/Merge Join/Union All feeding a single destination directly, not via a
        // Conditional Split -- that shape has its own chain-walking gate in ResolveBranch) --
        // discovered as a real, previously-SILENT failure: RBC_Demo_ETL's own
        // DFT_SortAndMergeJoin (a Merge Join over two Flat File Sources) used to pick just the
        // FIRST source found here, generate a row type from only ITS columns, and report no gap
        // at all -- only failing at BUILD time with a confusing "row type has no definition for
        // 'Email'" once Data Conversion support (2026-08-28) removed the ONE OTHER reason this
        // flow used to fail to translate. Caught by actually building the regenerated real
        // package's whole solution, not by the gap-count report alone. A single Data Flow Task
        // with two UNRELATED source/destination pairs (SyntheticParallelShapes.dtsx's own
        // DFT_DirectCopy) is also caught here, one step earlier than its pre-existing
        // "no Derived Column found" gate -- still correctly gapped, just with a clearer reason.
        var sourceComponents = pipeline.Components
            .Where(c => c.ComponentClassId == "Microsoft.FlatFileSource" || c.ComponentClassId == "Microsoft.ExcelSource" || SourceInfo.IsSqlSource(c))
            .ToList();
        if (sourceComponents.Count > 1)
        {
            var names = string.Join(", ", sourceComponents.Select(c => c.Name));
            gaps.Add(new GenerationGap(taskName,
                $"this Data Flow Task has {sourceComponents.Count} source components ({names}) -- a merge/join across multiple sources (or multiple independent source/destination pairs) is not supported yet"));
            return null;
        }

        var flatFileSource = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.FlatFileSource");
        var excelSource = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.ExcelSource");
        // OLE DB Source or ADO NET Source (Microsoft.DataReaderSourceAdapter, discriminated by
        // payload not ComponentClassId -- see AdoNetSourcePayload's own doc comment) -- either
        // way, a SQL-based source BuildSqlFlowSource resolves generically from here.
        var oleDbSource = pipeline.Components.FirstOrDefault(SourceInfo.IsSqlSource);
        var derivedColumn = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.DerivedColumn");
        var dataConversion = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.DataConvert");
        var lookup = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.Lookup");
        var conditionalSplitComponent = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.ConditionalSplit");

        AggregatePlan? aggregate = null;
        var aggregateComponent = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.Aggregate");
        if (aggregateComponent is not null)
        {
            aggregate = PlanAggregate(taskName, pipeline, aggregateComponent, gaps);
            if (aggregate is null) return null; // PlanAggregate already added the gap
        }

        ConditionalSplitPlan? conditionalSplit = null;
        if (conditionalSplitComponent is not null)
        {
            conditionalSplit = PlanConditionalSplit(taskName, pipeline, conditionalSplitComponent, gaps);
            if (conditionalSplit is null) return null; // PlanConditionalSplit already added the gap
        }

        // Mutually exclusive with Conditional Split -- only looked for when no split was found,
        // matching every real evidenced flow (each has one or the other, never both).
        MulticastPlan? multicast = null;
        if (conditionalSplit is null)
        {
            var multicastComponent = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.Multicast");
            if (multicastComponent is not null)
            {
                multicast = PlanMulticast(taskName, pipeline, multicastComponent, gaps);
                if (multicast is null) return null; // PlanMulticast already added the gap
            }
        }

        // Scoped to the plain single-source/single-destination shape only -- a Conditional
        // Split/Multicast branch's own mid-chain RowCount (as opposed to the already-handled
        // discarded-dead-end case) is left to ResolveBranch's existing generic "not supported"
        // fallthrough, unevidenced and unchanged by this round.
        List<RowCountPlan>? rowCounts = null;
        if (conditionalSplit is null && multicast is null)
        {
            rowCounts = ResolveRowCounts(taskName, pipeline, gaps);
            if (rowCounts is null) return null; // ResolveRowCounts already added the gap
        }

        var destination = conditionalSplit is not null
            ? conditionalSplit.Branches[^1].Destination // the default branch, resolved last
            : multicast is not null
                // Not simply [^1] any more -- a discarded (RowCount-dead-end) branch's own
                // Destination is null, and for the one real evidenced shape (LKP_Country's own
                // Multicast) that discarded branch IS the last one declared. Picking the first
                // NON-null destination is behaviorally identical to [^1] for every flow that
                // predates discard support (every branch there already had a real destination).
                ? multicast.Branches.Select(b => b.Destination).FirstOrDefault(d => d is not null)
                : pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.OLEDBDestination")
                    ?? pipeline.Components.FirstOrDefault(c => c.AdoNetDestination is not null)
                    ?? pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.FlatFileDestination");

        if (destination is null)
        {
            // No bulk-insert-shaped destination -- before giving up, check for the OTHER real
            // evidenced flow terminus: an OLE DB Command, which IS its own sink (one parameterized
            // SQL execution per row, no destination table at all). Only tried when the ordinary
            // destination search already failed, so every existing destination-based flow is
            // completely unaffected.
            var commandComponents = pipeline.Components.Where(c => c.ComponentClassId == "Microsoft.OLEDBCommand").ToList();
            // multicast must also be checked null here now -- a Multicast whose every branch is
            // discarded (no live branch at all) leaves `destination` null without implying no
            // Multicast was found, unlike before discard support existed.
            if (commandComponents.Count == 1 && conditionalSplit is null && multicast is null)
            {
                var oleDbCommandPlan = ResolveOleDbCommand(taskName, commandComponents[0], gaps);
                if (oleDbCommandPlan is null) return null; // ResolveOleDbCommand already added the gap
                // Multicast is never populated here -- destination being null already implies no
                // multicast was found either, per the mutual-exclusion check above.
                return new DataFlowPlan(taskName, pipeline, flatFileSource, oleDbSource, derivedColumn, lookup,
                    conditionalSplit, commandComponents[0], dataConversion, ExcelSource: excelSource, OleDbCommand: oleDbCommandPlan, Aggregate: aggregate, RowCounts: rowCounts);
            }

            gaps.Add(new GenerationGap(taskName,
                "no OLE DB Destination, ADO NET Destination, Flat File Destination, or OLE DB Command found in this Data Flow Task -- only [Flat File Source|OLE DB Source|ADO NET Source|Excel Source] -> [Derived Column] -> [OLE DB Destination|ADO NET Destination|Flat File Destination|OLE DB Command] is supported yet"));
            return null;
        }

        // Scoped to the plain single-source/single-destination shape only -- a Conditional
        // Split/Multicast branch's own Sort is a different, already-handled shape
        // (ResolveBranch's own pass-through widening), and a Lookup/Aggregate flow's own
        // destination-row-type resolution is different enough (RBC_Demo_ETL has no evidenced
        // Lookup/Aggregate+Sort combination) that a stray Sort there is left alone rather than
        // guessed at.
        SortKeyPlan? sortKey = null;
        if (conditionalSplit is null && multicast is null && aggregate is null && lookup is null)
        {
            var (hasGap, resolvedSort) = ResolveStandaloneSort(taskName, pipeline, destination, gaps);
            if (hasGap) return null; // ResolveStandaloneSort already added the gap
            sortKey = resolvedSort;
        }

        return new DataFlowPlan(taskName, pipeline, flatFileSource, oleDbSource, derivedColumn, lookup, conditionalSplit, destination, dataConversion, ExcelSource: excelSource, Multicast: multicast, Aggregate: aggregate, RowCounts: rowCounts, SortKey: sortKey);
    }

    /// <summary>Detects a standalone <c>Microsoft.Sort</c> in the plain (non-Conditional-Split,
    /// non-Multicast, non-Lookup, non-Aggregate, non-Merge-Join) single-destination flow shape
    /// and resolves its single ascending sort key -- gap-audit Phase 3.5 (2026-09-02). Returns
    /// <c>(false, null)</c> (no gap, nothing to apply) when no Sort exists in this pipeline at
    /// all, so every existing, already-verified fixture with no Sort is completely unaffected.
    /// Returns <c>(true, null)</c> -- a gap already added to <paramref name="gaps"/> -- when a
    /// Sort exists but cannot be safely reproduced: more than one Sort, an unresolvable/
    /// multi-column key, or a Sort whose own output does not provably reach the resolved
    /// destination. Silently proceeding in any of those cases would repeat the exact silent-drop
    /// bug this round fixes (confirmed real by generating a fixture with this shape BEFORE this
    /// method existed: a plain unconditional read, no ordering applied, no gap reported).</summary>
    private static (bool HasGap, SortKeyPlan? Sort) ResolveStandaloneSort(
        string taskName, PipelineSpec pipeline, PipelineComponentSpec destination, List<GenerationGap> gaps)
    {
        var sorts = pipeline.Components.Where(c => c.ComponentClassId == "Microsoft.Sort").ToList();
        if (sorts.Count == 0) return (false, null);

        if (sorts.Count > 1)
        {
            gaps.Add(new GenerationGap(taskName,
                $"this Data Flow Task has {sorts.Count} Sort components -- only exactly one, applying to the flow's own destination, is supported"));
            return (true, null);
        }

        var sort = sorts[0];
        if (!ComponentReachesForward(pipeline, sort, destination))
        {
            gaps.Add(new GenerationGap(taskName,
                $"Sort '{sort.Name}' does not lead to this flow's own resolved destination '{destination.Name}' -- not supported"));
            return (true, null);
        }

        return ResolveSortKeyForComponent(taskName, sort, gaps);
    }

    /// <summary>Resolves ONE <c>Microsoft.Sort</c> component's own single ascending key --
    /// extracted from <see cref="ResolveStandaloneSort"/> (gap-audit Phase 3.5) so
    /// <see cref="ResolveUnionSide"/> (gap-audit Phase 3.6, a <c>Microsoft.Merge</c> side's own
    /// upstream Sort) can reuse the identical resolution rather than duplicating it. Callers
    /// that need a reachability check first (e.g. <see cref="ResolveStandaloneSort"/>'s own
    /// "does this Sort lead to the resolved destination") do that BEFORE calling this -- this
    /// method only ever resolves the key itself.</summary>
    private static (bool HasGap, SortKeyPlan? Sort) ResolveSortKeyForComponent(
        string taskName, PipelineComponentSpec sort, List<GenerationGap> gaps)
    {
        var keys = sort.Sort?.Keys ?? [];
        if (keys.Count != 1)
        {
            gaps.Add(new GenerationGap(taskName,
                $"Sort '{sort.Name}' has {keys.Count} sort key(s) -- only exactly one (ascending) is supported"));
            return (true, null);
        }

        var keyColumn = sort.Outputs.SelectMany(o => o.Columns).FirstOrDefault(c => c.Name == keys[0].ColumnName);
        var keyClrType = keyColumn?.DataType is null ? null : SsisPipelineTypeMap.Resolve(keyColumn.DataType);
        if (keyColumn is null || keyClrType is null)
        {
            gaps.Add(new GenerationGap(taskName,
                $"Sort '{sort.Name}''s own key column '{keys[0].ColumnName}' could not be resolved to a known CLR type"));
            return (true, null);
        }

        return (false, new SortKeyPlan(sort, keys[0].ColumnName, keyClrType));
    }

    /// <summary>Breadth-first walk forward from <paramref name="from"/>'s own non-error output(s)
    /// through the pipeline's <c>&lt;paths&gt;</c> to see whether <paramref name="target"/> is
    /// reachable, hopping through any number of intermediate components (Derived Column, Data
    /// Conversion, another Sort, etc.) -- deliberately generic rather than special-cased to a
    /// known list of "pass-through" component types, since this only answers a structural
    /// reachability question, never a value-resolution one (that stays with the existing,
    /// unmodified name-based column resolution every destination emitter already uses).</summary>
    private static bool ComponentReachesForward(PipelineSpec pipeline, PipelineComponentSpec from, PipelineComponentSpec target)
    {
        var visited = new HashSet<string> { from.RefId };
        var queue = new Queue<string>(from.Outputs.Where(o => o.IsErrorOut != true).Select(o => o.RefId));

        while (queue.Count > 0)
        {
            var outputRefId = queue.Dequeue();
            foreach (var path in pipeline.Paths.Where(p => p.StartId == outputRefId))
            {
                var next = pipeline.Components.FirstOrDefault(c => c.Inputs.Any(i => i.RefId == path.EndId));
                if (next is null || !visited.Add(next.RefId)) continue;
                if (next.RefId == target.RefId) return true;

                foreach (var output in next.Outputs.Where(o => o.IsErrorOut != true)) queue.Enqueue(output.RefId);
            }
        }

        return false;
    }

    /// <summary>Resolves every <c>Microsoft.RowCount</c> in this flow's own pipeline into a
    /// <see cref="RowCountPlan"/> -- see <see cref="DataFlowPlan.RowCounts"/>'s own doc comment
    /// for the scope this is deliberately limited to. A RowCount with no (or empty)
    /// <c>VariableName</c> fails the whole flow, never silently dropping the side effect --
    /// unevidenced (every real instance seen so far declares one), but this tool never guesses a
    /// variable name.</summary>
    private static List<RowCountPlan>? ResolveRowCounts(string taskName, PipelineSpec pipeline, List<GenerationGap> gaps)
    {
        var rowCounts = new List<RowCountPlan>();
        foreach (var component in pipeline.Components.Where(c => c.ComponentClassId == "Microsoft.RowCount"))
        {
            var variableName = component.RowCount?.VariableName;
            if (string.IsNullOrEmpty(variableName))
            {
                gaps.Add(new GenerationGap(taskName, $"RowCount '{component.Name}' has no VariableName -- not supported"));
                return null;
            }

            rowCounts.Add(new RowCountPlan(component, variableName));
        }

        return rowCounts;
    }

    /// <summary>
    /// Resolves a <c>Microsoft.Aggregate</c> component into an <see cref="AggregatePlan"/> --
    /// built speculatively 2026-08-30 (Count only), widened 2026-09-02 (gap-audit Phase 3.3) to
    /// every measured AggregationType. Deliberately narrow: exactly one GroupBy column
    /// (AggregationType=0) plus one or more function columns (1=Count, 2=CountAll, 3=CountDistinct,
    /// 4=Sum, 5=Average, 6=Minimum, 7=Maximum -- see <c>AggregatePayload</c>'s own doc comment for
    /// how every raw value was measured), each resolved back to its own real source column name
    /// via <see cref="LineageBuilder"/>'s "Aggregate" edge kind -- except CountAll (2), which SSIS
    /// itself accepts with no <c>AggregationColumnId</c> at all (a genuine <c>COUNT(*)</c>,
    /// confirmed via a live object-model probe) and so is the one function column allowed to
    /// resolve with a null source. Any other raw AggregationType value, zero or more than one
    /// GroupBy column, or an unresolvable source reference for anything else is a generation gap,
    /// never a guess.
    /// </summary>
    private static AggregatePlan? PlanAggregate(string taskName, PipelineSpec pipeline, PipelineComponentSpec component, List<GenerationGap> gaps)
    {
        var payload = component.Aggregate;
        if (payload is null || payload.Columns.Count == 0)
        {
            gaps.Add(new GenerationGap(taskName, $"Aggregate '{component.Name}' has no output columns -- not supported"));
            return null;
        }

        var lineage = LineageBuilder.Build(pipeline);
        // A CountAll (2) column with no AggregationColumnId at all still gets one written by
        // SSIS -- a placeholder lineage reference (e.g. "#{Package\...\0:invalid}") that never
        // matches any real producer (confirmed real, gap-audit Phase 3.3, 2026-09-02: every
        // Aggregate output column persists this property unconditionally). LineageBuilder's own
        // MakeEdge resilience fallback (unrelated to this round) resolves an unmatched lineageId
        // to a sentinel FromComponentName of "(unresolved)" rather than dropping the edge -- so a
        // column-less CountAll's own "source" would otherwise resolve to that bogus placeholder
        // string instead of null. Recognized here, specific to this call site, rather than
        // widened generically -- every OTHER function type still correctly gaps on a genuinely
        // unresolvable reference via the same "(unresolved)" check.
        string? ResolveSourceColumnName(string outputColumnName)
        {
            var edge = lineage.Edges.FirstOrDefault(e =>
                e.Kind == "Aggregate" && e.ToComponentRefId == component.RefId && e.ToColumnName == outputColumnName);
            return edge is null || edge.FromComponentName == "(unresolved)" ? null : edge.FromColumnName;
        }

        var groupByColumns = payload.Columns.Where(c => c.AggregationTypeRaw == 0).ToList();
        var functionColumns = payload.Columns.Where(c => c.AggregationTypeRaw is >= 1 and <= 7).ToList();
        var otherColumns = payload.Columns.Where(c => c.AggregationTypeRaw is null or < 0 or > 7).ToList();

        if (otherColumns.Count > 0)
        {
            var names = string.Join(", ", otherColumns.Select(c => $"{c.OutputColumnName} (AggregationType={(c.AggregationTypeRaw?.ToString() ?? "(unknown)")})"));
            gaps.Add(new GenerationGap(taskName,
                $"Aggregate '{component.Name}' has column(s) using an unrecognized AggregationType: {names} -- only GroupBy (0), Count (1), CountAll (2), CountDistinct (3), Sum (4), Average (5), Minimum (6), and Maximum (7) are supported"));
            return null;
        }

        if (groupByColumns.Count != 1)
        {
            gaps.Add(new GenerationGap(taskName,
                $"Aggregate '{component.Name}' has {groupByColumns.Count} GroupBy column(s) -- only exactly one is supported"));
            return null;
        }

        if (functionColumns.Count == 0)
        {
            gaps.Add(new GenerationGap(taskName,
                $"Aggregate '{component.Name}' has no aggregated column -- a GroupBy with nothing aggregated is not supported"));
            return null;
        }

        var groupByCol = groupByColumns[0];
        var groupBySource = ResolveSourceColumnName(groupByCol.OutputColumnName);
        if (groupBySource is null)
        {
            gaps.Add(new GenerationGap(taskName,
                $"Aggregate '{component.Name}': GroupBy column '{groupByCol.OutputColumnName}' has no resolvable source column -- not supported"));
            return null;
        }

        var functions = new List<AggregateFunctionSpec>();
        foreach (var col in functionColumns)
        {
            var rawType = col.AggregationTypeRaw!.Value;
            var source = ResolveSourceColumnName(col.OutputColumnName);
            if (source is null && rawType != 2) // CountAll (2) is the one function SSIS allows with no AggregationColumnId
            {
                gaps.Add(new GenerationGap(taskName,
                    $"Aggregate '{component.Name}': column '{col.OutputColumnName}' has no resolvable source column -- not supported"));
                return null;
            }
            functions.Add(new AggregateFunctionSpec(col.OutputColumnName, source, rawType));
        }

        return new AggregatePlan(component, groupByCol.OutputColumnName, groupBySource, functions);
    }

    /// <summary>Resolves one <c>Microsoft.OLEDBCommand</c> component into an <see cref="OleDbCommandPlan"/>
    /// -- rewrites its raw SqlCommand text's <c>?</c> placeholders into EF Core's own "{0}", "{1}",
    /// ... positional raw-SQL syntax, matched against each input column's own bound external
    /// column (<see cref="OleDbCommandPayload.ColumnMappings"/>), ordered by that external
    /// column's own INDEX within the input's declared external-column list -- NOT by
    /// &lt;inputColumns&gt; declaration order, and NOT by parsing a name for an embedded number
    /// (see <see cref="OleDbCommandPlan"/>'s own doc comment for why both were tried and
    /// rejected). A non-empty ParameterMapping, a column with no resolvable external-column
    /// binding, a duplicate binding, non-contiguous positions, or a placeholder count that
    /// doesn't match the resolved binding count, is each a named gap -- none of these shapes is
    /// evidenced in any real package seen so far, and guessing would risk binding the wrong
    /// value to the wrong parameter.</summary>
    private static OleDbCommandPlan? ResolveOleDbCommand(string taskName, PipelineComponentSpec command, List<GenerationGap> gaps)
    {
        var payload = command.OleDbCommand;
        if (string.IsNullOrEmpty(payload?.SqlCommand))
        {
            gaps.Add(new GenerationGap(taskName, $"OLE DB Command '{command.Name}' has no SqlCommand text -- not supported"));
            return null;
        }

        if (!string.IsNullOrEmpty(payload.ParameterMapping))
        {
            gaps.Add(new GenerationGap(taskName,
                $"OLE DB Command '{command.Name}' has a non-empty ParameterMapping ('{payload.ParameterMapping}') -- this property has never been observed populated by real SSIS (a live probe found it absent even under an explicit non-default binding) and its real format is unknown, so it is not supported"));
            return null;
        }

        var placeholderCount = payload.SqlCommand.Count(ch => ch == '?');

        // True binding order is each bound external column's own position within the input's
        // <externalMetadataColumns> list -- confirmed real via two live object-model probes
        // (2026-09-02): a positional "Param_N" case and a real, evidenced named "@ParamName"
        // case (an EXEC stored-procedure call) both preserve true call order there, regardless
        // of <inputColumns> declaration order and regardless of whether the external column's
        // own name encodes a position at all.
        var mainInput = command.Inputs.FirstOrDefault();
        var externalColumnPosition = (mainInput?.ExternalMetadataColumns ?? [])
            .Select((ext, index) => (ext.Name, index))
            .ToDictionary(x => x.Name, x => x.index);

        var ordered = new SortedDictionary<int, string>();
        foreach (var param in payload.Parameters)
        {
            var mapping = payload.ColumnMappings.FirstOrDefault(m => m.ComponentColumnName == param.Name);
            if (mapping is null || !externalColumnPosition.TryGetValue(mapping.ExternalColumnName, out var position))
            {
                gaps.Add(new GenerationGap(taskName,
                    $"OLE DB Command '{command.Name}': input column '{param.Name}' has no resolvable placeholder binding -- not supported"));
                return null;
            }
            if (!ordered.TryAdd(position, param.Name))
            {
                gaps.Add(new GenerationGap(taskName,
                    $"OLE DB Command '{command.Name}': more than one input column is bound to the same placeholder position ({position}) -- not supported"));
                return null;
            }
        }

        if (placeholderCount != ordered.Count)
        {
            gaps.Add(new GenerationGap(taskName,
                $"OLE DB Command '{command.Name}' has {placeholderCount} '?' placeholder(s) but {ordered.Count} resolvably-bound input column(s) -- not supported"));
            return null;
        }

        if (ordered.Count > 0 && (ordered.Keys.First() != 0 || ordered.Keys.Last() != ordered.Count - 1))
        {
            gaps.Add(new GenerationGap(taskName,
                $"OLE DB Command '{command.Name}': resolved placeholder positions ({string.Join(",", ordered.Keys)}) are not a contiguous, 0-based sequence -- not supported"));
            return null;
        }

        var sqlTemplate = payload.SqlCommand;
        for (var i = 0; i < placeholderCount; i++)
        {
            var index = sqlTemplate.IndexOf('?');
            sqlTemplate = sqlTemplate[..index] + "{" + i + "}" + sqlTemplate[(index + 1)..];
        }

        return new OleDbCommandPlan(command, sqlTemplate, ordered.Values.ToList());
    }

    /// <summary>Resolves a genuinely multi-independent-source <c>Microsoft.Merge</c>/
    /// <c>Microsoft.UnionAll</c> -- gap-audit Phase 3.6 (2026-09-02). Every input is walked
    /// BACKWARD via <see cref="ResolveUnionSide"/> (Sort required only for Merge); fatal for the
    /// whole flow if any side or the destination fails to resolve, same "no half-finished
    /// implementations" rule <see cref="PlanMergeJoin"/> already follows.</summary>
    private static DataFlowPlan? PlanUnion(string taskName, PipelineSpec pipeline, PipelineComponentSpec unionComponent, List<GenerationGap> gaps)
    {
        var isMerge = unionComponent.ComponentClassId == "Microsoft.Merge";
        var inputs = unionComponent.Inputs;
        if (inputs.Count < 2)
        {
            gaps.Add(new GenerationGap(taskName,
                $"'{unionComponent.Name}' ({unionComponent.ComponentClassId}) has {inputs.Count} input(s) -- at least 2 are required"));
            return null;
        }

        // Microsoft.Merge is hard-capped at exactly 2 inputs by SSIS itself -- confirmed real:
        // attempting a 3rd via the object model crashes the native ReinitializeMetaData() with
        // an AccessViolationException (see BuildSortMergeRemergeFixture's own doc comment). A
        // saved .dtsx that somehow declares a different count is unevidenced, not guessed at.
        if (isMerge && inputs.Count != 2)
        {
            gaps.Add(new GenerationGap(taskName,
                $"Merge '{unionComponent.Name}' has {inputs.Count} inputs -- Microsoft.Merge is only ever evidenced with exactly 2, not supported"));
            return null;
        }

        var sides = new List<UnionSideSource>();
        foreach (var input in inputs)
        {
            var side = ResolveUnionSide(taskName, pipeline, unionComponent, input, sortRequired: isMerge, gaps);
            if (side is null) return null; // ResolveUnionSide already added the gap
            sides.Add(side);
        }

        // A Microsoft.Merge side's own key must resolve to the SAME CLR type on both sides --
        // MergeInterleaveRowSource<TRow,TKey> needs one shared TKey to compare across sides at
        // all, and SSIS itself would refuse to bind two differently-typed key columns together.
        if (isMerge && sides[0].SortKey?.KeyClrType.ClrTypeName != sides[1].SortKey?.KeyClrType.ClrTypeName)
        {
            gaps.Add(new GenerationGap(taskName,
                $"Merge '{unionComponent.Name}''s two sides sort by different CLR types ('{sides[0].SortKey?.KeyClrType.ClrTypeName}' vs '{sides[1].SortKey?.KeyClrType.ClrTypeName}') -- not supported"));
            return null;
        }

        var mainOutput = unionComponent.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (mainOutput is null)
        {
            gaps.Add(new GenerationGap(taskName, $"'{unionComponent.Name}' has no non-error output -- not supported"));
            return null;
        }

        // Resolve each side's own per-input column mapping (OutputColumnLineageID) and check it
        // covers the union's whole output. Before this, the mapping was never read at all and
        // ONE reader keyed on the union's OUTPUT column names was applied to every side's result
        // set -- which is only correct when every side happens to use those same names. A
        // rename produced a GetOrdinal throw at best, and a SWAPPED mapping silently transposed
        // that side's values, since a swap keeps every name resolvable.
        var outputColumnsByLineageId = mainOutput.Columns.ToDictionary(c => c.LineageId, c => c.Name, StringComparer.Ordinal);
        for (var i = 0; i < inputs.Count; i++)
        {
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var col in inputs[i].Columns)
            {
                if (col.OutputColumnLineageId is null ||
                    !outputColumnsByLineageId.TryGetValue(col.OutputColumnLineageId, out var outputColumnName))
                {
                    gaps.Add(new GenerationGap(taskName,
                        $"'{unionComponent.Name}' input '{inputs[i].Name}' column '{col.CachedName}' does not resolve to one of the component's own output columns via OutputColumnLineageID -- refusing to assume it maps to an output column of the same name"));
                    return null;
                }
                aliases[col.CachedName] = outputColumnName;
            }

            var mapped = new HashSet<string>(aliases.Values, StringComparer.Ordinal);
            var missing = mainOutput.Columns.Select(c => c.Name).Where(n => !mapped.Contains(n)).ToList();
            if (missing.Count > 0)
            {
                gaps.Add(new GenerationGap(taskName,
                    $"'{unionComponent.Name}' input '{inputs[i].Name}' does not map anything to output column(s) {string.Join(", ", missing)} -- every side must supply every output column"));
                return null;
            }

            sides[i] = sides[i] with { ColumnAliases = aliases };
        }

        var outPath = pipeline.Paths.FirstOrDefault(p => p.StartId == mainOutput.RefId);
        var destination = outPath is null ? null : pipeline.Components.FirstOrDefault(c => c.Inputs.Any(i => i.RefId == outPath.EndId));
        if (destination is null ||
            (destination.ComponentClassId != "Microsoft.OLEDBDestination" && destination.AdoNetDestination is null && destination.ComponentClassId != "Microsoft.FlatFileDestination"))
        {
            gaps.Add(new GenerationGap(taskName,
                $"'{unionComponent.Name}' does not connect directly to an OLE DB/ADO NET/Flat File Destination -- a transform after a multi-source Merge/UnionAll is not supported yet"));
            return null;
        }

        return new DataFlowPlan(taskName, pipeline, FlatFileSource: null, OleDbSource: null, DerivedColumn: null, Lookup: null,
            ConditionalSplit: null, DestinationComponent: destination, DataConversion: null,
            Union: new UnionPlan(unionComponent, isMerge, sides));
    }

    /// <summary>Walks backward from one input of a genuinely multi-independent-source Merge/
    /// UnionAll to the real source feeding it -- mirrors <see cref="ResolveMergeJoinSide"/>'s own
    /// backward-walk shape, generalized: <paramref name="sortRequired"/> requires an immediate
    /// upstream <c>Microsoft.Sort</c> (Merge) or allows a direct/Data-Conversion-only hop
    /// (UnionAll, which SSIS never requires sorted input for). Deliberately does NOT walk through
    /// a Flat File/Excel Source -- see <see cref="UnionPlan"/>'s own doc comment for why that's a
    /// stated scope trim, not an oversight: this round's own dtexec probes are both SQL-sourced,
    /// and a mixed-source-kind side would need its own row-reading machinery this round never
    /// built or verified.</summary>
    private static UnionSideSource? ResolveUnionSide(
        string taskName, PipelineSpec pipeline, PipelineComponentSpec unionComponent, PipelineInputSpec input, bool sortRequired, List<GenerationGap> gaps)
    {
        var sideName = string.IsNullOrEmpty(input.Name) ? input.RefId : input.Name;
        var representative = input.Columns.FirstOrDefault();
        if (representative is null)
        {
            gaps.Add(new GenerationGap(taskName, $"'{unionComponent.Name}''s input '{sideName}' has no columns -- not supported"));
            return null;
        }

        var producer = FindProducerComponent(pipeline, representative.LineageId);

        PipelineComponentSpec? sort = null;
        var afterSortInput = input;
        if (producer?.ComponentClassId == "Microsoft.Sort")
        {
            sort = producer;
            var sortInput = sort.Inputs.FirstOrDefault();
            if (sortInput is null || sortInput.Columns.Count == 0)
            {
                gaps.Add(new GenerationGap(taskName,
                    $"Sort '{sort.Name}' (feeding '{unionComponent.Name}''s input '{sideName}') has no input columns -- not supported"));
                return null;
            }
            afterSortInput = sortInput;
        }
        else if (sortRequired)
        {
            gaps.Add(new GenerationGap(taskName,
                $"'{unionComponent.Name}''s input '{sideName}' is fed by '{producer?.Name ?? "(unresolved)"}' ({producer?.ComponentClassId ?? "?"}), not a Sort -- Merge requires sorted input, only [Source] -> [Data Conversion] -> Sort -> Merge is supported"));
            return null;
        }

        // Same "check every column, not just the representative one" rule ResolveMergeJoinSide
        // already established -- a Data Conversion's own converted column can sit anywhere in
        // the buffer, not necessarily first.
        PipelineComponentSpec? trueSource = null;
        PipelineComponentSpec? dataConversion = null;
        foreach (var col in afterSortInput.Columns)
        {
            var colProducer = FindProducerComponent(pipeline, col.LineageId);
            if (colProducer is null) continue;
            if (colProducer.ComponentClassId == "Microsoft.FlatFileSource" || SourceInfo.IsSqlSource(colProducer))
                trueSource ??= colProducer;
            else if (colProducer.ComponentClassId == "Microsoft.DataConvert")
                dataConversion ??= colProducer;
        }

        // Data Conversion is detected (so a Merge/UnionAll side fed by one gets a precise gap
        // naming it) but NOT resolved through -- unlike ResolveMergeJoinSide, which has real
        // evidence for exactly this shape (RBC_Demo_ETL's own DFT_SortAndMergeJoin). No genuinely
        // multi-independent-source Merge/UnionAll with a Data Conversion on either side has ever
        // been evidenced or probed here -- a stated scope trim (see UnionPlan's own doc comment),
        // not a guess.
        if (dataConversion is not null)
        {
            gaps.Add(new GenerationGap(taskName,
                $"'{unionComponent.Name}''s input '{sideName}' is fed by a Data Conversion ('{dataConversion.Name}') -- not supported yet for a multi-independent-source Merge/UnionAll"));
            return null;
        }

        if (trueSource is null || !SourceInfo.IsSqlSource(trueSource))
        {
            var viaLabel = sort is not null ? $"Sort '{sort.Name}'" : $"'{unionComponent.Name}''s input '{sideName}'";
            gaps.Add(new GenerationGap(taskName,
                $"{viaLabel} is not fed by a recognizable SQL source (OLE DB/ADO NET) -- only a SQL-sourced side is supported yet for a multi-independent-source Merge/UnionAll"));
            return null;
        }

        SortKeyPlan? sortKey = null;
        if (sort is not null)
        {
            var (keyHasGap, resolvedKey) = ResolveSortKeyForComponent(taskName, sort, gaps);
            if (keyHasGap) return null; // ResolveSortKeyForComponent already added the gap
            sortKey = resolvedKey;
        }

        // ColumnAliases is filled in by PlanUnion once the union's own output columns are known
        // (this method resolves one side in isolation and has no view of them).
        return new UnionSideSource(trueSource, sort, dataConversion, sortKey, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>Resolves a Merge Join's own two independent source chains (each walked BACKWARD
    /// from the join through its own Sort and, if present, one Data Conversion, to a real
    /// source) plus its single forward destination -- confirmed real shape from RBC_Demo_ETL's
    /// own DFT_SortAndMergeJoin. Fatal for the whole flow if either side or the destination
    /// fails to resolve, same "no half-finished implementations" rule every other multi-part
    /// plan in this file follows.</summary>
    private static DataFlowPlan? PlanMergeJoin(string taskName, PipelineSpec pipeline, PipelineComponentSpec mergeJoin, List<GenerationGap> gaps)
    {
        var leftInput = mergeJoin.Inputs.FirstOrDefault(i => i.Name.Contains("Left", StringComparison.Ordinal));
        var rightInput = mergeJoin.Inputs.FirstOrDefault(i => i.Name.Contains("Right", StringComparison.Ordinal));
        if (leftInput is null || rightInput is null)
        {
            gaps.Add(new GenerationGap(taskName, $"Merge Join '{mergeJoin.Name}' does not have a recognizable Left/Right Input pair -- not supported"));
            return null;
        }

        var left = ResolveMergeJoinSide(taskName, pipeline, mergeJoin, leftInput, "Left", gaps);
        if (left is null) return null; // ResolveMergeJoinSide already added the gap
        var right = ResolveMergeJoinSide(taskName, pipeline, mergeJoin, rightInput, "Right", gaps);
        if (right is null) return null;

        var mainOutput = mergeJoin.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (mainOutput is null)
        {
            gaps.Add(new GenerationGap(taskName, $"Merge Join '{mergeJoin.Name}' has no non-error output -- not supported"));
            return null;
        }

        var outPath = pipeline.Paths.FirstOrDefault(p => p.StartId == mainOutput.RefId);
        var destination = outPath is null ? null : pipeline.Components.FirstOrDefault(c => c.Inputs.Any(i => i.RefId == outPath.EndId));
        if (destination is null ||
            (destination.ComponentClassId != "Microsoft.OLEDBDestination" && destination.AdoNetDestination is null))
        {
            // Flat File Destination is deliberately NOT accepted here even though the rest of
            // this tool supports it: GenerateMergeJoinFlow resolves its entity name via
            // TryResolveEntityName (a TABLE name) and hardcodes new SqlFlowSink(), so a Merge
            // Join landing in a flat file used to generate a SQL bulk-insert into a table that
            // does not exist -- broken code with no gap reported. Gap it until a flat-file sink
            // path exists here (gap-audit Phase 1, 2026-09-02).
            var what = destination is null
                ? "does not connect directly to a destination"
                : $"connects to '{destination.Name}' ({destination.ComponentClassId})";
            gaps.Add(new GenerationGap(taskName, $"Merge Join '{mergeJoin.Name}' {what} -- only a direct OLE DB/ADO NET Destination is supported (a transform after a Merge Join, or a Flat File Destination, is not)"));
            return null;
        }

        var joinType = ResolveMergeJoinType(taskName, mergeJoin, gaps);
        if (joinType is null) return null; // ResolveMergeJoinType already added the gap

        return new DataFlowPlan(taskName, pipeline, FlatFileSource: null, OleDbSource: null, DerivedColumn: null, Lookup: null,
            ConditionalSplit: null, DestinationComponent: destination, DataConversion: null, MergeJoin: new MergeJoinPlan(mergeJoin, left, right, joinType));
    }

    /// <summary>
    /// Maps <c>Microsoft.MergeJoin</c>'s own raw <c>JoinType</c> property to the
    /// <c>Etl.Core.Abstractions.MergeJoinType</c> member name to emit.
    /// <para><b>MEASURED against real SSIS via dtexec (2026-09-02), not inferred.</b> A probe
    /// package joined a left input keyed {1,2,3} to a right input keyed {2,4} (so exactly one
    /// key matches) once per raw value and the output rows were read back:</para>
    /// <list type="bullet">
    /// <item><c>0</c> -> 4 rows: every left row plus the unmatched right row = <b>FULL OUTER</b></item>
    /// <item><c>1</c> -> 3 rows: every left row, unmatched right dropped = <b>LEFT OUTER</b></item>
    /// <item><c>2</c> -> 1 row: the matching key only = <b>INNER</b></item>
    /// </list>
    /// <para><b>Only 1 and 2 are EMITTED.</b> Raw 0 is measured and understood but still
    /// gapped, because <c>MergeJoinEmitter</c> emits left-sourced columns as
    /// <c>left!.Column</c> (it passes <c>mayBeAbsent: isRightSide</c>) and a full outer join
    /// genuinely has no left row for a right-only key -- see the <c>case 0</c> arm below.</para>
    /// <para>Note this is the exact REVERSE of <c>MergeJoinType</c>'s own C# ordinal order
    /// (Inner=0, LeftOuter=1, FullOuter=2), so a naive <c>(MergeJoinType)raw</c> cast is wrong
    /// for 0 and 2 and right only for 1 -- which is why this maps explicitly. Before this
    /// existed, <c>ProgramEmitter</c> hardcoded <c>MergeJoinType.LeftOuter</c> for every Merge
    /// Join regardless of the raw value, so the one real evidenced package
    /// (RBC_Demo_ETL's Package_Transforms.dtsx, MRG_CustomerContacts, JoinType=2 = INNER)
    /// generated code that emitted unmatched left rows with a nulled right side that real SSIS
    /// drops entirely.</para>
    /// <para>The property is ALWAYS persisted, even when left at its designer default -- also
    /// measured: a probe package that never called <c>SetComponentProperty("JoinType", ...)</c>
    /// still saved <c>&lt;property name="JoinType"&gt;2&lt;/property&gt;</c> (and the live
    /// default read back as 2, i.e. Inner, matching SSIS's own documented default). So a null
    /// raw value means the property genuinely is not there, which is a gap rather than a
    /// default to fall back on.</para>
    /// </summary>
    private static string? ResolveMergeJoinType(string taskName, PipelineComponentSpec mergeJoin, List<GenerationGap> gaps)
    {
        var raw = mergeJoin.MergeJoin?.JoinTypeRaw;
        switch (raw)
        {
            case 1: return "LeftOuter";
            case 2: return "Inner";
            case 0:
                // Raw 0 IS full outer -- measured, see this method's own doc comment. It is
                // still gapped because MergeJoinEmitter cannot express it: that emitter passes
                // mayBeAbsent: isRightSide, so every LEFT-sourced column is emitted with a
                // null-forgiving "left!.Column". Under a full outer join the left row genuinely
                // IS absent for a right-only key, so the generated mapper would throw a
                // NullReferenceException on the first such row. Emitting FullOuter here without
                // that work would trade the old silent-wrong-rows bug for a silent crash.
                gaps.Add(new GenerationGap(
                    taskName,
                    $"Merge Join '{mergeJoin.Name}' declares JoinType=0 (FULL OUTER, measured against real SSIS). Etl.Core supports it, but the generated "
                        + "mapper assumes the left row is always present (it emits 'left!.Column'), which a full outer join violates for a right-only key -- "
                        + "so generating it would produce a NullReferenceException at runtime. Needs absent-left nullability in MergeJoinEmitter first."));
                return null;
            default:
                gaps.Add(new GenerationGap(
                    taskName,
                    $"Merge Join '{mergeJoin.Name}' declares JoinType={(raw is null ? "(absent)" : raw.ToString())}, which this tool has not measured against real SSIS -- "
                        + "only 0 (full outer), 1 (left outer) and 2 (inner) are confirmed. Refusing to guess a join type, since the wrong one silently emits or drops rows."));
                return null;
        }
    }

    /// <summary>Walks backward from one of a Merge Join's own inputs to the Sort feeding it
    /// (by matching the input's own representative column's LineageId against every component's
    /// output columns -- the same generic "find the producer by lineageId" resolution
    /// LineageBuilder itself uses, just walked by hand here since this is a component-structural
    /// question, not a column-value one), then one more hop back through the Sort's own input to
    /// either a real source or exactly one Data Conversion.</summary>
    private static MergeJoinSideSource? ResolveMergeJoinSide(
        string taskName, PipelineSpec pipeline, PipelineComponentSpec mergeJoin, PipelineInputSpec input, string sideName, List<GenerationGap> gaps)
    {
        var representative = input.Columns.FirstOrDefault();
        if (representative is null)
        {
            gaps.Add(new GenerationGap(taskName, $"Merge Join '{mergeJoin.Name}''s {sideName} Input has no columns -- not supported"));
            return null;
        }

        var sort = FindProducerComponent(pipeline, representative.LineageId);
        if (sort is null || sort.ComponentClassId != "Microsoft.Sort")
        {
            gaps.Add(new GenerationGap(taskName, $"Merge Join '{mergeJoin.Name}''s {sideName} Input is fed by '{sort?.Name ?? "(unresolved)"}' ({sort?.ComponentClassId ?? "?"}), not a Sort -- only [Source] -> [Data Conversion] -> Sort -> Merge Join is supported yet"));
            return null;
        }

        var sortInput = sort.Inputs.FirstOrDefault();
        if (sortInput is null || sortInput.Columns.Count == 0)
        {
            gaps.Add(new GenerationGap(taskName, $"Sort '{sort.Name}' (feeding Merge Join '{mergeJoin.Name}''s {sideName} Input) has no input columns -- not supported"));
            return null;
        }

        // Sort has exactly ONE upstream connection, but that connection's buffer can carry a MIX
        // of producers: plain passthrough columns whose LineageId traces straight to the real
        // source, and (if a Data Conversion sits between them) one converted column whose
        // LineageId traces to the Data Conversion instead -- both flow through the SAME physical
        // buffer since Data Conversion is synchronous. Checking only the FIRST input column
        // (as an earlier version of this method did) missed the Data Conversion entirely: the
        // real evidenced fixture's own first column happens to be a plain passthrough one. Every
        // column is checked here instead, so either producer is found regardless of column order.
        PipelineComponentSpec? trueSource = null;
        PipelineComponentSpec? dataConversion = null;
        foreach (var col in sortInput.Columns)
        {
            var producer = FindProducerComponent(pipeline, col.LineageId);
            if (producer is null) continue;
            if (producer.ComponentClassId == "Microsoft.FlatFileSource" || SourceInfo.IsSqlSource(producer))
                trueSource ??= producer;
            else if (producer.ComponentClassId == "Microsoft.DataConvert")
                dataConversion ??= producer;
        }

        if (dataConversion is not null)
        {
            var conversionInput = dataConversion.Inputs.FirstOrDefault();
            var conversionRepresentative = conversionInput?.Columns.FirstOrDefault();
            var sourceViaConversion = conversionRepresentative is null ? null : FindProducerComponent(pipeline, conversionRepresentative.LineageId);
            if (sourceViaConversion is not null && trueSource is not null && sourceViaConversion.RefId != trueSource.RefId)
            {
                gaps.Add(new GenerationGap(taskName, $"Sort '{sort.Name}' appears to be fed by two different sources ('{trueSource.Name}' directly and '{sourceViaConversion.Name}' via '{dataConversion.Name}') -- not supported"));
                return null;
            }
            trueSource ??= sourceViaConversion;
        }

        if (trueSource is null)
        {
            gaps.Add(new GenerationGap(taskName, $"Sort '{sort.Name}' is not fed by a recognizable source (directly, or via a single Data Conversion) -- not supported"));
            return null;
        }

        return new MergeJoinSideSource(trueSource, sort, dataConversion);
    }

    /// <summary>Finds whichever component in this pipeline owns an output column with this exact
    /// LineageId -- the generic "who produced this value" lookup every backward walk in
    /// <see cref="ResolveMergeJoinSide"/> needs, independent of whether a &lt;path&gt; directly
    /// connects the two components (same rule <see cref="Ssis.Extract.Dtsx.LineageBuilder"/>'s
    /// own doc comment establishes for column-level lineage).</summary>
    private static PipelineComponentSpec? FindProducerComponent(PipelineSpec pipeline, string lineageId) =>
        pipeline.Components.FirstOrDefault(c => c.Outputs.Any(o => o.Columns.Any(col => col.LineageId == lineageId)));

    /// <summary>Resolves every case (in EvaluationOrder) plus the default output to the OLE DB
    /// Destination directly downstream of it. Fatal for the whole flow if ANY branch fails to
    /// resolve -- no partial split generation, matching the same "no half-finished
    /// implementations" rule Lookup's own gate follows.</summary>
    private static ConditionalSplitPlan? PlanConditionalSplit(
        string taskName, PipelineSpec pipeline, PipelineComponentSpec splitComponent, List<GenerationGap> gaps)
    {
        var payload = splitComponent.ConditionalSplit;
        if (payload?.DefaultOutputName is null)
        {
            gaps.Add(new GenerationGap(taskName,
                $"Conditional Split '{splitComponent.Name}' has no recognizable default output -- not supported"));
            return null;
        }

        var branches = new List<ConditionalSplitBranchPlan>();
        foreach (var @case in payload.Cases)
        {
            var branch = ResolveBranch(pipeline, splitComponent, @case.OutputName, @case.FriendlyExpression, taskName, gaps);
            if (branch is null) return null; // ResolveBranch already added the gap
            branches.Add(branch);
        }

        var defaultBranch = ResolveBranch(pipeline, splitComponent, payload.DefaultOutputName, friendlyExpression: null, taskName, gaps);
        if (defaultBranch is null) return null;
        branches.Add(defaultBranch);

        return new ConditionalSplitPlan(splitComponent, branches);
    }

    /// <summary>Resolves a Multicast: every non-error output is its own unconditional branch (no
    /// evaluation order, no default -- reuses the exact same forward chain-walk
    /// <see cref="ResolveBranch"/> already uses for Conditional Split, just called once per
    /// output with no condition attached).</summary>
    private static MulticastPlan? PlanMulticast(string taskName, PipelineSpec pipeline, PipelineComponentSpec multicastComponent, List<GenerationGap> gaps)
    {
        // A Multicast auto-provisions a fresh "spare" output the instant an existing one gets a
        // path attached -- confirmed real via an object-model probe (see
        // Ssis.Extract.FixtureBuilder's own BuildMulticastFixture): a component with N real
        // branches always has N+1 outputs, the trailing one permanently unconnected and marked
        // dangling="true" in the saved XML. Skipped here, not treated as a branch to resolve.
        var outputs = multicastComponent.Outputs.Where(o => o.IsErrorOut != true && o.Dangling != true).ToList();
        if (outputs.Count == 0)
        {
            gaps.Add(new GenerationGap(taskName, $"Multicast '{multicastComponent.Name}' has no outputs -- not supported"));
            return null;
        }

        var branches = new List<ConditionalSplitBranchPlan>();
        foreach (var output in outputs)
        {
            var branch = ResolveBranch(pipeline, multicastComponent, output.Name, friendlyExpression: null, taskName, gaps, componentKind: "Multicast");
            if (branch is null) return null; // ResolveBranch already added the gap
            branches.Add(branch);
        }

        return new MulticastPlan(multicastComponent, branches);
    }

    /// <summary>A branch's own chain to its destination is capped at this many hops so a
    /// malformed/cyclic pipeline graph degrades to a gap instead of an infinite loop -- no real
    /// package evidenced here needs more than 2 (one tag Derived Column, one Union All).</summary>
    private const int MaxBranchChainHops = 8;

    private static ConditionalSplitBranchPlan? ResolveBranch(
        PipelineSpec pipeline, PipelineComponentSpec splitComponent, string outputName, string? friendlyExpression,
        string taskName, List<GenerationGap> gaps, string componentKind = "Conditional Split")
    {
        var output = splitComponent.Outputs.FirstOrDefault(o => o.Name == outputName);
        if (output is null)
        {
            gaps.Add(new GenerationGap(taskName,
                $"{componentKind} output '{outputName}' on '{splitComponent.Name}' was not found among its own outputs"));
            return null;
        }

        // Walk forward from the split's own output, through any chain of Derived Column
        // (recorded, since it may compute a destination column), Union All, Sort, and/or Merge
        // (all three are pure physical/ordering pass-throughs -- confirmed real from
        // RBC_Demo_ETL's own DFT_MergeSortedBranches: Sort's own output columns keep the exact
        // same names as its input, and Merge's own single output likewise keeps the same names
        // as both of its inputs, so neither needs any special value-resolution here -- the
        // eventual destination-column resolution in TransformEmitter/EntityEmitter already
        // matches purely by buffer column NAME, with zero awareness that a Sort/Merge sat in
        // between) until an OLE DB Destination is reached. Two branches that both pass through
        // the SAME Union All/Merge naturally resolve to the SAME destination instance here, with
        // no special-casing needed for that convergence -- Merge's own two distinct inputs
        // ("Merge Input 1"/"Merge Input 2") both belong to the identical component instance, so
        // whichever branch's path ends at either one still resolves `next` to that same instance.
        var derivedColumns = new List<PipelineComponentSpec>();
        var currentOutputRefId = output.RefId;

        for (var hop = 0; hop < MaxBranchChainHops; hop++)
        {
            var path = pipeline.Paths.FirstOrDefault(p => p.StartId == currentOutputRefId);
            if (path is null)
            {
                gaps.Add(new GenerationGap(taskName,
                    $"{componentKind} output '{outputName}' on '{splitComponent.Name}' does not connect to anything downstream -- not supported"));
                return null;
            }

            var next = pipeline.Components.FirstOrDefault(c => c.Inputs.Any(i => i.RefId == path.EndId));
            if (next is null)
            {
                gaps.Add(new GenerationGap(taskName,
                    $"{componentKind} output '{outputName}' on '{splitComponent.Name}' connects to an input this tool could not resolve to a component -- not supported"));
                return null;
            }

            // Gated to Multicast only (added 2026-09-02, RBC_Demo_ETL's own
            // DFT_LookupAndAggregate): a dead-end Microsoft.RowCount -- one with no outgoing
            // path of its own -- is a valid, non-fatal branch terminus, not a gap. Confirmed
            // real: the one evidenced instance (LKP_Country's Multicast, MCAST_Matched) fans a
            // second output straight to a RowCount that writes User::MatchedRows, a package
            // variable nothing else in the whole 5614-line package ever reads (grepped directly,
            // not assumed). Deliberately NOT extended to Conditional Split -- its own "fatal for
            // the whole flow if ANY branch fails" invariant stays exactly as documented, since
            // this shape is unevidenced there and a split's branches are meant to be mutually
            // exclusive routing decisions, not "some branches are just counters".
            if (componentKind == "Multicast" && next.ComponentClassId == "Microsoft.RowCount"
                && !pipeline.Paths.Any(p => next.Outputs.Any(o => o.RefId == p.StartId)))
            {
                var variableName = next.Properties.FirstOrDefault(p => p.Name == "VariableName")?.Value;
                gaps.Add(new GenerationGap(taskName,
                    $"{componentKind} '{splitComponent.Name}' output '{outputName}' counts rows into '{variableName ?? "(unknown variable)"}' via RowCount '{next.Name}', which nothing else in this package reads -- this count is not reproduced in generated code",
                    IsBlocking: false));
                return new ConditionalSplitBranchPlan(outputName, friendlyExpression, derivedColumns, Destination: null, Discarded: true);
            }

            // Widened to include Flat File Destination for Multicast (added 2026-08-28): the real
            // evidenced shape (RBC_Demo_ETL's own DFT_FixedWidthImport) fans one Multicast to a
            // Flat File Destination AND an OLE DB Destination from the same component -- no
            // Conditional Split fixture ever needed this branch target before, but the check is a
            // strict widening (adds an acceptance case, restricts nothing), so it's safe here too.
            if (next.ComponentClassId is "Microsoft.OLEDBDestination" or "Microsoft.FlatFileDestination" || next.AdoNetDestination is not null)
                return new ConditionalSplitBranchPlan(outputName, friendlyExpression, derivedColumns, next);

            // "Microsoft.Aggregate" is gated to Multicast only, added 2026-09-02 alongside the
            // discard recognition above -- purely structural (this walk doesn't inspect column
            // identity, only where the chain eventually connects), so it's deliberately NOT added
            // to derivedColumns the way a real passthrough is: an Aggregate changes the row shape
            // entirely (fewer rows, a new column set), and that value-level resolution is handled
            // entirely by the already-existing, independently-invoked PlanAggregate/
            // AggregateRowEmitter/GenerateAggregateFlow machinery (reused directly by whichever
            // Lookup/Aggregate composition consumes this Multicast -- see DataFlowPlan.Lookup's
            // own doc comment), not reimplemented here. Safe to widen to Conditional Split too by
            // the same reasoning, but kept Multicast-only since it is unevidenced there.
            if (next.ComponentClassId is "Microsoft.DerivedColumn" or "Microsoft.UnionAll" or "Microsoft.Sort" or "Microsoft.Merge"
                || (componentKind == "Multicast" && next.ComponentClassId == "Microsoft.Aggregate"))
            {
                if (next.ComponentClassId == "Microsoft.DerivedColumn")
                    derivedColumns.Add(next);

                var nextOutput = next.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
                if (nextOutput is null)
                {
                    gaps.Add(new GenerationGap(taskName,
                        $"{componentKind} output '{outputName}' on '{splitComponent.Name}' passes through '{next.Name}', which has no non-error output -- not supported"));
                    return null;
                }

                currentOutputRefId = nextOutput.RefId;
                continue;
            }

            gaps.Add(new GenerationGap(taskName,
                $"{componentKind} output '{outputName}' on '{splitComponent.Name}' passes through '{next.Name}' ({next.ComponentClassId}) before reaching a destination -- not supported"));
            return null;
        }

        gaps.Add(new GenerationGap(taskName,
            $"{componentKind} output '{outputName}' on '{splitComponent.Name}' does not reach an OLE DB Destination within {MaxBranchChainHops} hops -- not supported"));
        return null;
    }
}
