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
    AggregatePlan? Aggregate = null);

/// <summary>A resolved <c>Microsoft.OLEDBCommand</c> flow -- <see cref="SqlTemplate"/> has every
/// <c>?</c> placeholder rewritten to EF Core's own "{0}", "{1}", ... raw-SQL parameter syntax,
/// positionally matching <see cref="ParameterColumnNames"/> (the command's own declared input
/// columns, in order -- the well-documented default when the component's ParameterMapping
/// property is empty; a non-empty one is a named gap in <see cref="PackagePlanner"/>, never
/// guessed).</summary>
public sealed record OleDbCommandPlan(
    PipelineComponentSpec Component,
    string SqlTemplate,
    IReadOnlyList<string> ParameterColumnNames);

/// <summary>One Count output column of an Aggregate, resolved back to the real SOURCE column
/// it counts (via <c>Ssis.Extract.Dtsx.LineageBuilder</c>'s new "Aggregate" edge kind) -- e.g.
/// <c>CustomerCount</c> counting non-NULL values of the source's own <c>CustomerID</c> column
/// (confirmed via a real dtexec run that AggregationType=1 excludes NULLs, matching ordinary SQL
/// <c>COUNT(column)</c>, not <c>COUNT(*)</c> -- see <c>AggregatePayload</c>'s own doc comment).</summary>
public sealed record AggregateCountSpec(string OutputColumnName, string SourceColumnName);

/// <summary>A resolved <c>Microsoft.Aggregate</c> flow, built speculatively 2026-08-30 -- see
/// <c>Ssis.Extract.Model.Pipeline.AggregatePayload</c>'s own doc comment for why. Scoped
/// deliberately to exactly the evidenced shape: one GroupBy column
/// (<see cref="GroupByOutputColumnName"/>, resolved back to its own real source column via
/// lineage) plus one or more Count columns. Any other AggregationType, zero or more than one
/// GroupBy column, or an unresolvable source reference is a generation gap, not a guess -- see
/// <see cref="PackagePlanner"/>'s own <c>PlanAggregate</c>.</summary>
public sealed record AggregatePlan(
    PipelineComponentSpec Component,
    string GroupByOutputColumnName,
    string GroupBySourceColumnName,
    List<AggregateCountSpec> Counts);

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
    MergeJoinSideSource Right);

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
/// entity/table emission and disambiguates the per-branch transform class names.</summary>
public sealed record ConditionalSplitBranchPlan(string OutputName, string? FriendlyExpression, List<PipelineComponentSpec> DerivedColumns, PipelineComponentSpec Destination);

/// <summary>One Conditional Split, fully resolved: every case in EvaluationOrder, THEN the
/// default branch last (<see cref="Branches"/>[^1]) -- this ordering is what RouterEmitter and
/// ProgramEmitter both rely on to build the if/else-if chain and the branch-index list without
/// re-deriving which entry is the default.</summary>
public sealed record ConditionalSplitPlan(PipelineComponentSpec Component, List<ConditionalSplitBranchPlan> Branches);

/// <summary>One Multicast, fully resolved: every output is an unconditional branch (no
/// evaluation order, no default -- every row goes to every branch). Reuses
/// <see cref="ConditionalSplitBranchPlan"/> for the branch shape (destination + any per-branch
/// Derived Column/Union-All/Sort/Merge chain, via the same <c>ResolveBranch</c> walker) with
/// <c>FriendlyExpression</c> always null -- there is no condition to translate, so
/// <see cref="PackageGenerator"/>'s Multicast emission never calls RouterEmitter at all.</summary>
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
public abstract record PackageStep;
public sealed record FlowStep(DataFlowPlan Flow) : PackageStep;
public sealed record SqlStep(string TaskName, string Sql) : PackageStep;
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
    List<GenerationGap> Gaps);

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

        WalkContainer(package.Executables, package.Dag, package.PrecedenceConstraints, package.ConnectionManagers,
            preLoadStatements, preLoadFileActions, flows, steps, gaps, containerIsPostFlow: false, emitSeams: emitSeams);

        ReportParallelismFlattening(package, gaps);
        ReportConditionalConstraints(package, gaps);

        return new PackagePlan(preLoadStatements, preLoadFileActions, flows, steps, gaps);
    }

    /// <summary>
    /// A precedence constraint can be conditional: it fires only on a specific outcome
    /// (<c>DTS:Value</c> -- Success/Failure/Completion) and/or only when an expression is true
    /// (<c>DTS:EvalOp</c> + <c>DTS:Expression</c>). The generated <c>PackageRunner</c> runs every
    /// step unconditionally, so a conditional edge means generated code runs work SSIS would have
    /// skipped -- or, for a Failure edge, work SSIS runs ONLY when something has already gone
    /// wrong.
    ///
    /// Found the same way the disabled-executable bug was: both attributes have always been
    /// extracted (<see cref="PrecedenceConstraintSpec.Value"/>/<see cref="PrecedenceConstraintSpec.EvalOp"/>)
    /// and read by nothing in this project. RBC_Demo_ETL's own Package has one of each --
    /// <c>DFT_LoadCustomers -&gt; SQL_LogLoadFailure</c> on Failure, and
    /// <c>SCR_NotifyAndLogProgress -&gt; DFT_BuildSalesSummary</c> gated on
    /// <c>@[User::RowsLoaded] &gt; 0</c> -- so generated code would write a failure-log row on
    /// every SUCCESSFUL run and build the sales summary regardless of whether anything loaded.
    ///
    /// BLOCKING, unlike the parallelism and disabled advisories. Those two produce code that does
    /// the right work (just sequentially, or minus something SSIS also skipped); this one produces
    /// code that does the WRONG work. Reported rather than implemented because conditional
    /// execution is a real feature -- <c>IEtlPackage.Steps</c> is a flat unconditional list, and
    /// the expression form additionally needs a variable the generated code can only have if the
    /// Script Task that sets it has been ported.
    ///
    /// Raw enum values are interpreted from the real GAC enums (<c>DTSExecResult</c>,
    /// <c>DTSPrecedenceEvalOp</c>), read by reflection rather than guessed -- the two enums use
    /// different numbering bases, which is exactly the kind of thing this project does not assume.
    /// </summary>
    private static void ReportConditionalConstraints(PackageSpec package, List<GenerationGap> gaps)
    {
        var packageName = package.ObjectName ?? "package";

        Report(package.PrecedenceConstraints);
        foreach (var container in EnumerateContainers(package.Executables))
            Report(container.PrecedenceConstraints);

        void Report(List<PrecedenceConstraintSpec> constraints)
        {
            foreach (var constraint in constraints)
            {
                // Absent means the schema default, which is the unconditional case for both:
                // DTSExecResult.Success = 0, DTSPrecedenceEvalOp.Constraint = 2.
                var outcome = constraint.Value switch
                {
                    null or "0" => null,
                    "1" => "only when the preceding executable FAILS (DTSExecResult.Failure)",
                    "2" => "on completion, pass or fail (DTSExecResult.Completion)",
                    var raw => $"only on outcome DTS:Value=\"{raw}\"",
                };

                var expression = constraint.EvalOp switch
                {
                    null or "2" => null,
                    "1" => $"only when its expression is true (`{constraint.Expression}`), the constraint itself being ignored",
                    "3" => $"only when its expression is ALSO true (`{constraint.Expression}`)",
                    "4" => $"when the constraint holds OR its expression is true (`{constraint.Expression}`)",
                    var raw => $"conditionally on DTS:EvalOp=\"{raw}\" with expression `{constraint.Expression}`",
                };

                if (outcome is null && expression is null) continue;

                var conditions = string.Join(", and ", new[] { outcome, expression }.Where(c => c is not null));
                var from = LeafName(constraint.From);
                var to = LeafName(constraint.To);
                gaps.Add(new GenerationGap($"{packageName}.{from}-{to}.Constraint",
                    $"the precedence constraint '{from}' -> '{to}' fires {conditions}; the generated PackageRunner " +
                    $"runs every step unconditionally, so '{to}' would run when SSIS would not have run it. " +
                    "IEtlPackage.Steps is a flat unconditional list -- conditional execution is not generated, and " +
                    "is not guessed at either."));
            }
        }
    }

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
        List<GenerationGap> gaps, bool containerIsPostFlow, bool emitSeams)
    {
        var byRefId = children.ToDictionary(e => e.RefId);

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
                    preLoadStatements, preLoadFileActions, flows, steps, gaps, containerIsPostFlow: isPostFlow, emitSeams: emitSeams);
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
                    steps.Add(loopStep);
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
                    steps.Add(new SqlStep(executable.ObjectName ?? refId, sqlTask.SqlStatementSource));
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
                    steps.Add(new FileSystemStep(executable.ObjectName ?? refId, action));
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
                    steps.Add(new FlowStep(flow));
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
                steps.Add(new ScriptTaskStep(executable));

                // A ported Script Task's own SQL runs inside the package transaction, so it is
                // never a "flow" -- but it CAN follow one, and anything after it must still see
                // itself as post-flow. Propagate whatever position it inherited rather than
                // resetting it.
                if (isPostFlow) flowOrAfter.Add(refId);
                else preFlowScriptTasks.Add(executable);
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

        var destination = conditionalSplit is not null
            ? conditionalSplit.Branches[^1].Destination // the default branch, resolved last
            : multicast is not null
                ? multicast.Branches[^1].Destination
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
            if (commandComponents.Count == 1 && conditionalSplit is null)
            {
                var oleDbCommandPlan = ResolveOleDbCommand(taskName, commandComponents[0], gaps);
                if (oleDbCommandPlan is null) return null; // ResolveOleDbCommand already added the gap
                // Multicast is never populated here -- destination being null already implies no
                // multicast was found either, per the mutual-exclusion check above.
                return new DataFlowPlan(taskName, pipeline, flatFileSource, oleDbSource, derivedColumn, lookup,
                    conditionalSplit, commandComponents[0], dataConversion, ExcelSource: excelSource, OleDbCommand: oleDbCommandPlan, Aggregate: aggregate);
            }

            gaps.Add(new GenerationGap(taskName,
                "no OLE DB Destination, ADO NET Destination, Flat File Destination, or OLE DB Command found in this Data Flow Task -- only [Flat File Source|OLE DB Source|ADO NET Source|Excel Source] -> [Derived Column] -> [OLE DB Destination|ADO NET Destination|Flat File Destination|OLE DB Command] is supported yet"));
            return null;
        }

        return new DataFlowPlan(taskName, pipeline, flatFileSource, oleDbSource, derivedColumn, lookup, conditionalSplit, destination, dataConversion, ExcelSource: excelSource, Multicast: multicast, Aggregate: aggregate);
    }

    /// <summary>
    /// Resolves a <c>Microsoft.Aggregate</c> component into an <see cref="AggregatePlan"/> --
    /// built speculatively 2026-08-30, see <c>AggregatePayload</c>'s own doc comment. Deliberately
    /// narrow: exactly one GroupBy column (AggregationType=0) plus one or more Count columns
    /// (AggregationType=1), each resolved back to its own real source column name via
    /// <see cref="LineageBuilder"/>'s "Aggregate" edge kind. Any other AggregationType value, zero
    /// or more than one GroupBy column, or an unresolvable source reference is a generation gap,
    /// never a guess -- none of Sum/Average/Minimum/Maximum/CountDistinct/CountAll is evidenced
    /// anywhere in the tracked portfolio, and none was measured.
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
        string? ResolveSourceColumnName(string outputColumnName) =>
            lineage.Edges.FirstOrDefault(e =>
                e.Kind == "Aggregate" && e.ToComponentRefId == component.RefId && e.ToColumnName == outputColumnName)?.FromColumnName;

        var groupByColumns = payload.Columns.Where(c => c.AggregationTypeRaw == 0).ToList();
        var countColumns = payload.Columns.Where(c => c.AggregationTypeRaw == 1).ToList();
        var otherColumns = payload.Columns.Where(c => c.AggregationTypeRaw is not (0 or 1)).ToList();

        if (otherColumns.Count > 0)
        {
            var names = string.Join(", ", otherColumns.Select(c => $"{c.OutputColumnName} (AggregationType={(c.AggregationTypeRaw?.ToString() ?? "(unknown)")})"));
            gaps.Add(new GenerationGap(taskName,
                $"Aggregate '{component.Name}' has column(s) using an unsupported AggregationType: {names} -- only GroupBy (0) and Count (1) are supported"));
            return null;
        }

        if (groupByColumns.Count != 1)
        {
            gaps.Add(new GenerationGap(taskName,
                $"Aggregate '{component.Name}' has {groupByColumns.Count} GroupBy column(s) -- only exactly one is supported"));
            return null;
        }

        if (countColumns.Count == 0)
        {
            gaps.Add(new GenerationGap(taskName,
                $"Aggregate '{component.Name}' has no Count column -- a GroupBy with nothing aggregated is not supported"));
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

        var counts = new List<AggregateCountSpec>();
        foreach (var col in countColumns)
        {
            var source = ResolveSourceColumnName(col.OutputColumnName);
            if (source is null)
            {
                gaps.Add(new GenerationGap(taskName,
                    $"Aggregate '{component.Name}': Count column '{col.OutputColumnName}' has no resolvable source column -- not supported"));
                return null;
            }
            counts.Add(new AggregateCountSpec(col.OutputColumnName, source));
        }

        return new AggregatePlan(component, groupByCol.OutputColumnName, groupBySource, counts);
    }

    /// <summary>Resolves one <c>Microsoft.OLEDBCommand</c> component into an <see cref="OleDbCommandPlan"/>
    /// -- rewrites its raw SqlCommand text's <c>?</c> placeholders into EF Core's own "{0}", "{1}",
    /// ... positional raw-SQL syntax, matched against the component's own declared input columns
    /// in DECLARATION order (the well-documented default when ParameterMapping is empty).
    /// A non-empty ParameterMapping, or a placeholder count that doesn't match the declared input
    /// column count, is a named gap -- neither is evidenced in any real package seen so far, and
    /// guessing a remapping would risk binding the wrong value to the wrong parameter.</summary>
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
                $"OLE DB Command '{command.Name}' has a non-empty ParameterMapping ('{payload.ParameterMapping}') -- only the declaration-order default (empty ParameterMapping) is supported"));
            return null;
        }

        var placeholderCount = payload.SqlCommand.Count(ch => ch == '?');
        if (placeholderCount != payload.Parameters.Count)
        {
            gaps.Add(new GenerationGap(taskName,
                $"OLE DB Command '{command.Name}' has {placeholderCount} '?' placeholder(s) but {payload.Parameters.Count} declared input column(s) -- not supported"));
            return null;
        }

        var sqlTemplate = payload.SqlCommand;
        for (var i = 0; i < placeholderCount; i++)
        {
            var index = sqlTemplate.IndexOf('?');
            sqlTemplate = sqlTemplate[..index] + "{" + i + "}" + sqlTemplate[(index + 1)..];
        }

        return new OleDbCommandPlan(command, sqlTemplate, payload.Parameters.Select(p => p.Name).ToList());
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
            (destination.ComponentClassId != "Microsoft.OLEDBDestination" && destination.AdoNetDestination is null && destination.ComponentClassId != "Microsoft.FlatFileDestination"))
        {
            gaps.Add(new GenerationGap(taskName, $"Merge Join '{mergeJoin.Name}' does not connect directly to an OLE DB/ADO NET/Flat File Destination -- a transform (e.g. Derived Column) after a Merge Join is not supported yet"));
            return null;
        }

        return new DataFlowPlan(taskName, pipeline, FlatFileSource: null, OleDbSource: null, DerivedColumn: null, Lookup: null,
            ConditionalSplit: null, DestinationComponent: destination, DataConversion: null, MergeJoin: new MergeJoinPlan(mergeJoin, left, right));
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

            // Widened to include Flat File Destination for Multicast (added 2026-08-28): the real
            // evidenced shape (RBC_Demo_ETL's own DFT_FixedWidthImport) fans one Multicast to a
            // Flat File Destination AND an OLE DB Destination from the same component -- no
            // Conditional Split fixture ever needed this branch target before, but the check is a
            // strict widening (adds an acceptance case, restricts nothing), so it's safe here too.
            if (next.ComponentClassId is "Microsoft.OLEDBDestination" or "Microsoft.FlatFileDestination" || next.AdoNetDestination is not null)
                return new ConditionalSplitBranchPlan(outputName, friendlyExpression, derivedColumns, next);

            if (next.ComponentClassId is "Microsoft.DerivedColumn" or "Microsoft.UnionAll" or "Microsoft.Sort" or "Microsoft.Merge")
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
