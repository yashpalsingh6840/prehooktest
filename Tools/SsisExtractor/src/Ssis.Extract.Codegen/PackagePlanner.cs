using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

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
    MergeJoinPlan? MergeJoin = null);

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

/// <summary>One resolved File System Task action -- <see cref="Operation"/> is one of the
/// <c>Etl.Core.Abstractions.FileSystemOperation</c> enum names this generator supports
/// (Copy/Move/Delete/Rename/CreateDirectory), never the raw SSIS enum name verbatim.
/// <see cref="SourcePath"/>/<see cref="DestinationPath"/> are already-resolved literal paths --
/// a FILE connection manager's own <c>ConnectionString</c> when the task referenced one (the
/// evidenced RBC_Demo_ETL shape), or the task's own raw path text otherwise. A variable-driven
/// source/destination is a gate <see cref="PackagePlanner"/> reports, not something this record
/// represents -- there is no runtime-config mapping for an SSIS variable to resolve against.</summary>
public sealed record FileSystemActionPlan(string Operation, string SourcePath, string? DestinationPath, bool Overwrite);

/// <summary>One step after pre-load: a Data Flow Task, an Execute SQL Task, or a File System
/// Task that runs AFTER at least one Data Flow Task has already run (a pre-load one of either
/// never appears here -- it only ever lands in <see cref="PackagePlan.PreLoadStatements"/>/
/// <see cref="PackagePlan.PreLoadFileActions"/>).</summary>
public abstract record PackageStep;
public sealed record FlowStep(DataFlowPlan Flow) : PackageStep;
public sealed record SqlStep(string TaskName, string Sql) : PackageStep;
public sealed record FileSystemStep(string TaskName, FileSystemActionPlan Action) : PackageStep;

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
/// A ForEach/For Loop container (<c>STOCK:FOREACHLOOP</c>) is NOT walked the same way and
/// reports its own explicit gap instead -- unlike Sequence, a loop has real per-iteration
/// semantics (an enumerator, variable mappings) that change what "run these children" means,
/// and <c>Etl.Core</c> has no loop abstraction to generate against (plan's own scope table).
/// Silently flattening a loop's children would generate code that runs each of them exactly
/// once instead of per iteration -- wrong, not just incomplete.
///
/// An executable type that is none of Sequence/ForEach/Data Flow/Execute SQL, or a Data Flow
/// Task with no OLE DB Destination, is reported as a gap rather than silently skipped.
/// </summary>
public static class PackagePlanner
{
    public static PackagePlan Plan(PackageSpec package)
    {
        var gaps = new List<GenerationGap>();
        var preLoadStatements = new List<string>();
        var preLoadFileActions = new List<FileSystemActionPlan>();
        var flows = new List<DataFlowPlan>();
        var steps = new List<PackageStep>();

        WalkContainer(package.Executables, package.Dag, package.PrecedenceConstraints, package.ConnectionManagers,
            preLoadStatements, preLoadFileActions, flows, steps, gaps, containerIsPostFlow: false);

        return new PackagePlan(preLoadStatements, preLoadFileActions, flows, steps, gaps);
    }

    /// <summary>Returns true if, by the time this container finishes (from its parent's point
    /// of view), a Data Flow Task has run -- either because <paramref name="containerIsPostFlow"/>
    /// was already true coming in, or because one ran somewhere inside.</summary>
    private static bool WalkContainer(
        List<ExecutableSpec> children, ControlFlowDagSpec dag, List<PrecedenceConstraintSpec> constraints,
        List<ConnectionManagerSpec> connectionManagers,
        List<string> preLoadStatements, List<FileSystemActionPlan> preLoadFileActions,
        List<DataFlowPlan> flows, List<PackageStep> steps,
        List<GenerationGap> gaps, bool containerIsPostFlow)
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

        foreach (var refId in order)
        {
            if (!byRefId.TryGetValue(refId, out var executable))
            {
                gaps.Add(new GenerationGap(refId, "topological order references an executable not found in its own container"));
                continue;
            }

            var isPostFlow = predecessorsByRefId.TryGetValue(refId, out var preds)
                ? preds.Any(flowOrAfter.Contains)
                : containerIsPostFlow;

            if (executable.ExecutableType == "STOCK:SEQUENCE")
            {
                var childRanAfterFlow = WalkContainer(executable.Children, executable.Dag, executable.PrecedenceConstraints, connectionManagers,
                    preLoadStatements, preLoadFileActions, flows, steps, gaps, containerIsPostFlow: isPostFlow);
                if (childRanAfterFlow)
                {
                    flowOrAfter.Add(refId);
                    sawDataFlowInThisContainer = true;
                }
                continue;
            }

            if (executable.ExecutableType == "STOCK:FOREACHLOOP")
            {
                gaps.Add(new GenerationGap(executable.ObjectName ?? refId,
                    "ForEach Loop containers are not supported -- Etl.Core has no loop abstraction to generate against"));
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

            gaps.Add(new GenerationGap(executable.ObjectName ?? refId,
                $"executable type '{executable.ExecutableType}' is not supported by this planner yet -- only Data Flow Task, Execute SQL Task, and File System Task are"));
        }

        return containerIsPostFlow || sawDataFlowInThisContainer;
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
            .Where(c => c.ComponentClassId == "Microsoft.FlatFileSource" || SourceInfo.IsSqlSource(c))
            .ToList();
        if (sourceComponents.Count > 1)
        {
            var names = string.Join(", ", sourceComponents.Select(c => c.Name));
            gaps.Add(new GenerationGap(taskName,
                $"this Data Flow Task has {sourceComponents.Count} source components ({names}) -- a merge/join across multiple sources (or multiple independent source/destination pairs) is not supported yet"));
            return null;
        }

        var flatFileSource = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.FlatFileSource");
        // OLE DB Source or ADO NET Source (Microsoft.DataReaderSourceAdapter, discriminated by
        // payload not ComponentClassId -- see AdoNetSourcePayload's own doc comment) -- either
        // way, a SQL-based source BuildSqlFlowSource resolves generically from here.
        var oleDbSource = pipeline.Components.FirstOrDefault(SourceInfo.IsSqlSource);
        var derivedColumn = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.DerivedColumn");
        var dataConversion = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.DataConvert");
        var lookup = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.Lookup");
        var conditionalSplitComponent = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.ConditionalSplit");

        ConditionalSplitPlan? conditionalSplit = null;
        if (conditionalSplitComponent is not null)
        {
            conditionalSplit = PlanConditionalSplit(taskName, pipeline, conditionalSplitComponent, gaps);
            if (conditionalSplit is null) return null; // PlanConditionalSplit already added the gap
        }

        var destination = conditionalSplit is not null
            ? conditionalSplit.Branches[^1].Destination // the default branch, resolved last
            : pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.OLEDBDestination")
                ?? pipeline.Components.FirstOrDefault(c => c.AdoNetDestination is not null)
                ?? pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.FlatFileDestination");

        if (destination is null)
        {
            gaps.Add(new GenerationGap(taskName,
                "no OLE DB Destination, ADO NET Destination, or Flat File Destination found in this Data Flow Task -- only [Flat File Source|OLE DB Source|ADO NET Source] -> [Derived Column] -> [OLE DB Destination|ADO NET Destination|Flat File Destination] is supported yet"));
            return null;
        }

        return new DataFlowPlan(taskName, pipeline, flatFileSource, oleDbSource, derivedColumn, lookup, conditionalSplit, destination, dataConversion);
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

    /// <summary>A branch's own chain to its destination is capped at this many hops so a
    /// malformed/cyclic pipeline graph degrades to a gap instead of an infinite loop -- no real
    /// package evidenced here needs more than 2 (one tag Derived Column, one Union All).</summary>
    private const int MaxBranchChainHops = 8;

    private static ConditionalSplitBranchPlan? ResolveBranch(
        PipelineSpec pipeline, PipelineComponentSpec splitComponent, string outputName, string? friendlyExpression,
        string taskName, List<GenerationGap> gaps)
    {
        var output = splitComponent.Outputs.FirstOrDefault(o => o.Name == outputName);
        if (output is null)
        {
            gaps.Add(new GenerationGap(taskName,
                $"Conditional Split output '{outputName}' on '{splitComponent.Name}' was not found among its own outputs"));
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
                    $"Conditional Split output '{outputName}' on '{splitComponent.Name}' does not connect to anything downstream -- not supported"));
                return null;
            }

            var next = pipeline.Components.FirstOrDefault(c => c.Inputs.Any(i => i.RefId == path.EndId));
            if (next is null)
            {
                gaps.Add(new GenerationGap(taskName,
                    $"Conditional Split output '{outputName}' on '{splitComponent.Name}' connects to an input this tool could not resolve to a component -- not supported"));
                return null;
            }

            if (next.ComponentClassId == "Microsoft.OLEDBDestination" || next.AdoNetDestination is not null)
                return new ConditionalSplitBranchPlan(outputName, friendlyExpression, derivedColumns, next);

            if (next.ComponentClassId is "Microsoft.DerivedColumn" or "Microsoft.UnionAll" or "Microsoft.Sort" or "Microsoft.Merge")
            {
                if (next.ComponentClassId == "Microsoft.DerivedColumn")
                    derivedColumns.Add(next);

                var nextOutput = next.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
                if (nextOutput is null)
                {
                    gaps.Add(new GenerationGap(taskName,
                        $"Conditional Split output '{outputName}' on '{splitComponent.Name}' passes through '{next.Name}', which has no non-error output -- not supported"));
                    return null;
                }

                currentOutputRefId = nextOutput.RefId;
                continue;
            }

            gaps.Add(new GenerationGap(taskName,
                $"Conditional Split output '{outputName}' on '{splitComponent.Name}' passes through '{next.Name}' ({next.ComponentClassId}) before reaching a destination -- not supported"));
            return null;
        }

        gaps.Add(new GenerationGap(taskName,
            $"Conditional Split output '{outputName}' on '{splitComponent.Name}' does not reach an OLE DB Destination within {MaxBranchChainHops} hops -- not supported"));
        return null;
    }
}
