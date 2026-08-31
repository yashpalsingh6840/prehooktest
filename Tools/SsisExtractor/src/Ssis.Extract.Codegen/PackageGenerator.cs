using System.Text.RegularExpressions;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen;

public sealed record PackageGenerateResult(
    string PackageName,
    List<GeneratedFile> Files,
    List<GenerationGap> Gaps,
    string? TargetServer = null,
    string? TargetDatabase = null);

/// <summary>
/// One package's worth of orchestration over the 8 emitters + <see cref="PackagePlanner"/>:
/// plan the control flow, decide names for the generated types (derived from the
/// destination table name -- the one identifier a .dtsx actually carries that's fit to
/// become a C# type name), then call each emitter with the pieces it asks for. A flow that
/// fails an early gate (no OpenRowset, not fast-load, no Flat File Source) still lets every
/// OTHER flow in the package generate -- same per-item isolation ctl.usp_DispatchRunItems
/// uses, applied here to flows instead of packages.
///
/// Pulled out of `ssisx generate`'s CLI shell (GenerateCommand.cs) specifically so it can be
/// unit- and golden-file-tested without going through PackageLoader/file I/O -- the same
/// "pure function returning GeneratedFiles, command is a thin shell" split every other
/// emitter in this project already follows.
/// </summary>
public static partial class PackageGenerator
{
    /// <param name="decisions">
    /// Confirmed Tier-1 answers (see <see cref="GapDecisions"/>) -- today just Lookup join keys.
    /// Optional so every existing caller and test is unaffected; absent or unconfirmed, the gaps
    /// they would have closed are reported exactly as before.
    /// </param>
    public static PackageGenerateResult Generate(PackageSpec package, string? namespacePrefix, GapDecisions? decisions = null, bool emitSeams = false)
    {
        decisions ??= GapDecisions.None;
        var ns = namespacePrefix is null ? package.ObjectName : $"{namespacePrefix}.{package.ObjectName}";
        var files = new List<GeneratedFile>();
        var gaps = new List<GenerationGap>();

        var plan = PackagePlanner.Plan(package);
        gaps.AddRange(plan.Gaps);

        // A ForEach-Data-Flow-Loop's own inner Data Flow Task is deliberately NOT duplicated
        // into plan.Flows (see PackagePlan.Flows' own doc comment -- it exists purely as a
        // convenience projection of ordinary FlowSteps), so this gate must also check for one
        // directly or a package whose ONLY real content is such a loop would be wrongly
        // reported as having nothing to generate at all.
        if (plan.Flows.Count == 0 && !plan.Steps.Any(s => s is ForEachDataFlowLoopStep))
        {
            gaps.Add(new GenerationGap(package.ObjectName, "no Data Flow Task could be planned for this package -- nothing to generate"));
            return new PackageGenerateResult(package.ObjectName, files, gaps);
        }

        var allExecutables = PackageTree.AllExecutables(package).ToList();
        var primaryKeysByDestination = PrimaryKeyInference.Infer(package, allExecutables)
            .ToDictionary(pk => pk.DestinationComponentRefId);

        var tables = new List<DbContextEmitter.TableSpec>();
        var wiredFlows = new Dictionary<DataFlowPlan, ProgramFlowSpec>();
        var wiredSplits = new Dictionary<DataFlowPlan, ProgramConditionalSplitStep>();
        var wiredMulticasts = new Dictionary<DataFlowPlan, ProgramMulticastStep>();
        var wiredOleDbCommands = new Dictionary<DataFlowPlan, ProgramOleDbCommandStep>();
        var wiredDataFlowLoops = new Dictionary<ForEachFileDataFlowPlan, ProgramForEachDataFlowLoopStep>();
        var fileSourceEntries = new List<FileSourceEntryRequest>();
        var functionsUsed = new HashSet<string>();
        string? authMode = null;
        string? userId = null;
        string? targetServer = null;
        string? targetDatabase = null;

        foreach (var flow in plan.Flows)
        {
            // Checked before ConditionalSplit and before any entity-name/source resolution --
            // SyntheticLookupSplit.dtsx has BOTH a Lookup and a Conditional Split in the same
            // flow, and the Lookup's own unrecoverable join key makes the whole flow unsafe to
            // wire regardless of what's downstream, so it must win. LookupCacheEmitter needs
            // only the Lookup component itself, not entityName/DestinationComponent, so hoisting
            // this check ahead of everything else changes nothing about its own behavior.
            if (flow.Lookup is { } lookup)
            {
                var cacheClassName = SanitizeIdentifier(lookup.Name) + "Cache";
                Merge(files, gaps, LookupCacheEmitter.Emit($"{ns}.Mapping", cacheClassName, lookup));

                // The join key IS persisted after all -- on the Lookup's own input column, as a
                // JoinToReferenceColumn custom property (see TryDeriveLookupJoinKey). A confirmed
                // decision is only the FALLBACK, for a Lookup that genuinely lacks it.
                var lookupGapId = GapIdentity.ComputeId(package.ObjectName, GapKind.LookupJoinKey, flow.TaskName);
                var joinKey = TryDeriveLookupJoinKey(lookup)
                              ?? decisions.ResolveLookupJoinKey(lookupGapId, currentEvidenceSha256: null);
                if (joinKey is { } resolvedJoinKey)
                {
                    var lookupFlow = GenerateLookupFlow(
                        package, ns, flow, lookup, cacheClassName, resolvedJoinKey, fileSourceEntries, files, tables,
                        functionsUsed, primaryKeysByDestination, gaps,
                        ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams);
                    if (lookupFlow is not null)
                    {
                        wiredFlows[flow] = lookupFlow;
                        continue;
                    }
                    // Fell through: GenerateLookupFlow recorded exactly which part of the shape it
                    // could not handle. Deliberately does NOT re-report the generic
                    // "no join key" gap below -- the key WAS supplied; something else is missing.
                    continue;
                }

                gaps.Add(new GenerationGap(flow.TaskName,
                    $"Lookup '{lookup.Name}' declares no join key: none of its input columns carries a JoinToReferenceColumn property, which is where SSIS records it (an SSDT-authored Lookup always has one -- a Lookup built through the object model without mapping its input columns does not). This tool will not guess a join key: a wrong one compiles, runs, and silently produces wrong joined data on every row. A reference-table cache scaffold was generated at Mapping/{cacheClassName}.cs, but this flow's transform/Program.cs wiring were NOT. To close it: answer this flow's own work packet under gaps/ and record the confirmed key in fills/{package.ObjectName}.decisions.json -- the whole flow is then generated deterministically, with no hand-written code at all.",
                    // Tier 1: the ONE thing missing here is a datum (which reference column the
                    // input joins against), not code -- LookupCacheEmitter already emits a cache with
                    // a generic LoadAsync<TKey> designed to defer exactly this. Once the key is
                    // confirmed, the deterministic emitter can generate the whole flow itself.
                    Kind: GapKind.LookupJoinKey,
                    EvidenceRefId: lookup.RefId));
                continue;
            }

            // Checked right after Lookup, before ConditionalSplit/MergeJoin/etc. -- built
            // speculatively 2026-08-30 (see AggregatePlan's own doc comment). An Aggregate's own
            // output is a structurally different row shape from the flow's raw source (fewer
            // rows, new column set), so it must be resolved on its own dedicated path rather
            // than the ordinary single-destination flow below.
            if (flow.Aggregate is { } aggregate)
            {
                var aggregateFlow = GenerateAggregateFlow(
                    package, ns, flow, aggregate, fileSourceEntries, files, tables, functionsUsed,
                    primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams);
                if (aggregateFlow is not null) wiredFlows[flow] = aggregateFlow;
                continue;
            }

            if (flow.ConditionalSplit is { } split)
            {
                var splitStep = GenerateConditionalSplitFlow(
                    package, ns, flow, split, fileSourceEntries, files, tables, functionsUsed,
                    primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams);
                if (splitStep is not null) wiredSplits[flow] = splitStep;
                continue;
            }

            if (flow.MergeJoin is { } mergeJoin)
            {
                var mergeJoinFlow = GenerateMergeJoinFlow(
                    package, ns, flow, mergeJoin, fileSourceEntries, files, tables, functionsUsed,
                    primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams);
                if (mergeJoinFlow is not null) wiredFlows[flow] = mergeJoinFlow;
                continue;
            }

            if (flow.Multicast is { } multicast)
            {
                var multicastStep = GenerateMulticastFlow(
                    package, ns, flow, multicast, fileSourceEntries, files, tables, functionsUsed,
                    primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams);
                if (multicastStep is not null) wiredMulticasts[flow] = multicastStep;
                continue;
            }

            // No destination table/entity/EF at all -- the command itself is the flow's sink, so
            // this bypasses every SQL-destination-shaped step below entirely (fast-load gate,
            // EntityEmitter, DbContextEmitter.TableSpec, TransformEmitter) the same way the
            // Lookup path bypasses them.
            if (flow.OleDbCommand is { } oleDbCommandPlan)
            {
                var oleDbCommandStep = GenerateOleDbCommandFlow(
                    package, ns, flow, oleDbCommandPlan, fileSourceEntries, files, gaps,
                    ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams);
                if (oleDbCommandStep is not null) wiredOleDbCommands[flow] = oleDbCommandStep;
                continue;
            }

            // A Flat File Destination has no OpenRowset/table at all, so it gets a DIFFERENT
            // entity-name source (the task's own name, always present) and skips every
            // SQL-shaped gate/step below (fast-load, EF Core table, the "no Derived Column"
            // direct-copy restriction) that assumes a destination TABLE exists.
            var isFlatFileDestination = flow.DestinationComponent.ComponentClassId == "Microsoft.FlatFileDestination";

            var entityName = isFlatFileDestination
                ? SanitizeIdentifier(flow.TaskName)
                : TryResolveEntityName(flow.TaskName, flow.DestinationComponent, gaps);
            if (entityName is null) continue;

            // Milestone 1 covers fast-load destinations only (plan §0.3's own resolution --
            // AccessMode itself is never decoded, this is the minimal check that decision
            // settled on). Anything else needs row-by-row insert generation this tool doesn't
            // have yet. A Flat File Destination has no fast-load concept at all -- it's always
            // "supported" in that sense, so this gate simply doesn't apply to it.
            if (!isFlatFileDestination && !DestinationInfo.IsFastLoadConfigured(flow.DestinationComponent))
            {
                gaps.Add(new GenerationGap(flow.TaskName, $"destination '{flow.DestinationComponent.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
                continue;
            }

            var nullableColumnNames = ResolveNullableColumnNames(flow);

            var sourceResult = ResolveFlowSource(package, ns, entityName, flow, flow.DestinationComponent, fileSourceEntries, files, gaps, nullableColumnNames);
            if (sourceResult is null) continue; // ResolveFlowSource already added the reason
            var (rowTypeName, programSource) = sourceResult.Value;

            var transformClassName = entityName + "Transform";
            Merge(files, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, flow.DestinationComponent, nullableColumnNames));

            // A direct-copy pipeline (no Derived Column/Data Conversion at all) was, until
            // 2026-08-28, ungenerated for an OLE DB/ADO NET destination -- flagged as "a separate,
            // still-unproven generator path for a SQL destination" purely because nothing had
            // ever built+run one. Flat File Destination got this exemption earlier (its own two
            // real evidenced instances, RBC_Demo_ETL's own DFT_ExportDelimited/DFT_ExportFixedWidth,
            // are BOTH pure column-copy exports), and TransformEmitter.Emit already handles an
            // empty DerivedColumns/DataConversions list correctly for ANY destination shape --
            // every column just becomes a plain `row.ColumnName` passthrough assignment, nothing
            // SQL-specific about it. RBC_Demo_ETL's own DFT_ExcelImport (Excel Source straight to
            // an OLE DB Destination, no Derived Column) is now what proves it for a SQL
            // destination too: generated, built against a copy of Etl.Core (0 warnings/0 errors),
            // and actually run against .\SQLFORPOC_2022, landing all 9 real rows from
            // DripEligibility.xlsx unchanged -- see Tools/SsisExtractor/CLAUDE.md's own "Excel
            // Source" section. So this gate is gone entirely now, not just widened.

            FlowSinkSpec sink;
            if (isFlatFileDestination)
            {
                var flatFileSink = ResolveFlatFileSink(package, flow.TaskName, flow.DestinationComponent, entityName, fileSourceEntries, gaps);
                if (flatFileSink is null) continue; // ResolveFlatFileSink already added the reason
                sink = flatFileSink;
            }
            else
            {
                primaryKeysByDestination.TryGetValue(flow.DestinationComponent.RefId, out var primaryKey);
                tables.Add(new DbContextEmitter.TableSpec(entityName, flow.DestinationComponent, primaryKey));
                sink = new SqlFlowSink();
            }

            var transformResult = TransformEmitter.Emit(new TransformRequest(
                EmitSeams: emitSeams,
                MappingNamespace: $"{ns}.Mapping",
                TransformClassName: transformClassName,
                RowTypeNamespace: programSource is CsvFlowSource ? $"{ns}.Csv" : programSource is ExcelFlowSource ? $"{ns}.Excel" : $"{ns}.Sql",
                RowTypeName: rowTypeName,
                EntityNamespace: $"{ns}.Model",
                EntityName: entityName,
                SsisFnNamespace: $"{ns}.Ssis",
                Pipeline: flow.Pipeline,
                DerivedColumns: flow.DerivedColumn is null ? [] : [flow.DerivedColumn],
                DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
                DestinationComponent: flow.DestinationComponent,
                NullableColumnNames: nullableColumnNames));
            Merge(files, gaps, transformResult.Result);
            foreach (var fn in transformResult.SsisFunctionsUsed) functionsUsed.Add(fn);

            if (transformResult.Result.Files.Count == 0)
            {
                // TransformEmitter itself already recorded why (e.g. every column untranslatable) --
                // don't also wire a Program.cs flow around a transform class that doesn't exist.
                continue;
            }

            wiredFlows[flow] = new ProgramFlowSpec(flow.TaskName, programSource, rowTypeName, entityName, transformClassName, sink);

            if (!isFlatFileDestination && authMode is null && ResolveDatabaseAuth(package, flow.DestinationComponent) is { } auth)
            {
                (authMode, userId, targetServer, targetDatabase) = auth;
            }
        }

        // A ForEach-Data-Flow-Loop's own inner flow is not in plan.Flows (see the gate above),
        // so it needs its own generation loop here -- built speculatively 2026-08-30, see
        // GenerateForEachDataFlowLoop's own doc comment.
        foreach (var dataFlowLoopStep in plan.Steps.OfType<ForEachDataFlowLoopStep>())
        {
            var programLoop = GenerateForEachDataFlowLoop(
                package, ns, dataFlowLoopStep.Loop, files, tables, functionsUsed,
                primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase, emitSeams);
            if (programLoop is not null) wiredDataFlowLoops[dataFlowLoopStep.Loop] = programLoop;
        }

        // A package whose only successfully-wired flows are Flat File Destinations has
        // tables.Count == 0 but still needs a (now possibly-empty) DbContext class -- Program.cs
        // always emits AddEtlDbContext<T>()/IUnitOfWork's own transaction wrapping needs a real
        // DbContext type regardless of whether anything maps to a SQL table (see
        // DbContextEmitter's own updated doc comment). Still correctly skipped when NOTHING
        // wired at all (an all-gapped package), matching existing behavior.
        // An OLE DB Command flow contributes no table of its own, but its own OleDbCommandStep
        // still runs through IUnitOfWork.ExecuteSqlAsync -- the same shared, DbContext-backed
        // connection/transaction as everything else in the package -- so a package whose ONLY
        // successfully-wired flow is an OLE DB Command still needs a (possibly table-less)
        // DbContext generated, same reasoning as the Flat File Destination case right above.
        if (tables.Count > 0 || wiredFlows.Count > 0 || wiredSplits.Count > 0 || wiredMulticasts.Count > 0 || wiredOleDbCommands.Count > 0 || wiredDataFlowLoops.Count > 0)
            Merge(files, gaps, DbContextEmitter.Emit($"{ns}.Model", $"{package.ObjectName}DbContext", tables));

        Merge(files, gaps, SsisFnEmitter.Emit($"{ns}.Ssis", functionsUsed));

        // Built by walking plan.Steps (not wiredFlows.Values) so a SqlStep between two flows
        // keeps its real position -- a flow that never made it into wiredFlows/wiredSplits/
        // wiredMulticasts/wiredOleDbCommands (failed a gate above) simply contributes nothing
        // here, same as it always has.
        var programSteps = new List<ProgramStep>();
        foreach (var step in plan.Steps)
        {
            switch (step)
            {
                case FlowStep { Flow: var flow } when wiredFlows.TryGetValue(flow, out var programFlow):
                    programSteps.Add(new ProgramFlowStep(programFlow));
                    break;
                case FlowStep { Flow: var flow } when wiredSplits.TryGetValue(flow, out var programSplit):
                    programSteps.Add(programSplit);
                    break;
                case FlowStep { Flow: var flow } when wiredMulticasts.TryGetValue(flow, out var programMulticast):
                    programSteps.Add(programMulticast);
                    break;
                case FlowStep { Flow: var flow } when wiredOleDbCommands.TryGetValue(flow, out var programOleDbCommand):
                    programSteps.Add(programOleDbCommand);
                    break;
                case SqlStep sqlStep:
                    programSteps.Add(new ProgramSqlStep(sqlStep.TaskName, sqlStep.Sql));
                    break;
                case FileSystemStep fileSystemStep:
                    programSteps.Add(new ProgramFileSystemStep(fileSystemStep.TaskName, fileSystemStep.Action));
                    break;
                case ForEachFileLoopStep loopStep:
                {
                    var programLoop = ResolveForEachFileLoop(loopStep.Loop, gaps);
                    if (programLoop is not null) programSteps.Add(programLoop);
                    break;
                }
                case ForEachDataFlowLoopStep { Loop: var dataFlowLoop } when wiredDataFlowLoops.TryGetValue(dataFlowLoop, out var programDataFlowLoop):
                    programSteps.Add(programDataFlowLoop);
                    break;
            }
        }

        if (wiredFlows.Count > 0 || wiredSplits.Count > 0 || wiredMulticasts.Count > 0 || wiredOleDbCommands.Count > 0 || wiredDataFlowLoops.Count > 0)
        {
            Merge(files, gaps, ProgramEmitter.Emit(new ProgramRequest(
                PackageName: package.ObjectName,
                RootNamespace: ns,
                DbContextTypeName: $"{package.ObjectName}DbContext",
                PreLoadStatements: plan.PreLoadStatements,
                PreLoadFileActions: plan.PreLoadFileActions,
                Steps: programSteps)));

            if (authMode is null)
            {
                gaps.Add(new GenerationGap(package.ObjectName, "could not resolve the target connection manager's auth mode -- defaulting to Windows auth in the generated appsettings.json; verify manually"));
                authMode = "Windows";
            }

            Merge(files, gaps, ProjectEmitter.Emit(new ProjectRequest(
                PackageName: package.ObjectName,
                FileSourceEntries: fileSourceEntries,
                DatabaseAuth: new DatabaseAuthRequest(authMode, userId),
                OnSuccessRecipients: [],
                OnFailureRecipients: [])));

            gaps.Add(new GenerationGap($"{package.ObjectName}.Notification", "a .dtsx carries no notification-recipient information -- OnSuccessRecipients/OnFailureRecipients were generated empty; fill in appsettings.json manually", IsBlocking: false));
        }

        return new PackageGenerateResult(package.ObjectName, files, gaps, targetServer, targetDatabase);
    }

    private static void Merge(List<GeneratedFile> files, List<GenerationGap> gaps, EmitResult result)
    {
        files.AddRange(result.Files);
        gaps.AddRange(result.Gaps);
    }

    /// <summary>The destination's own OpenRowset table name is the one identifier in a .dtsx
    /// actually fit to become a C# type name -- "[dbo].[Employee]" -> "Employee". Deliberately
    /// duplicates DbContextEmitter's own ParseOpenRowset (a three-line gate) rather than
    /// sharing it: this call site needs only the table name, DbContextEmitter needs the schema
    /// too, and threading a shared "parsed OpenRowset" type through both for three lines isn't
    /// worth it. Takes the destination directly (not a whole DataFlowPlan) so a Conditional
    /// Split's per-branch destinations can reuse it too.</summary>
    private static string? TryResolveEntityName(string taskName, PipelineComponentSpec destinationComponent, List<GenerationGap> gaps)
    {
        var openRowset = DestinationInfo.TableName(destinationComponent);
        if (string.IsNullOrEmpty(openRowset))
        {
            gaps.Add(new GenerationGap(taskName, "destination has no OpenRowset -- cannot derive an entity name for a SQL-command destination"));
            return null;
        }

        var parts = openRowset.Split("].[", StringSplitOptions.None);
        return parts.Length == 2 ? parts[1].TrimEnd(']') : openRowset.Trim('[', ']');
    }

    private static ConnectionManagerSpec? FindConnectionManager(PackageSpec package, string name) =>
        package.ConnectionManagers.FirstOrDefault(cm => cm.ObjectName == name);

    /// <summary>"Lookup Customer" -> "LookupCustomer" -- a component's own display name is
    /// free text a designer author can type anything into (spaces, punctuation), unlike
    /// entityName (always derived from a table's OpenRowset, already alphanumeric). Strips
    /// everything but letters/digits/underscore and guards against a leading digit, which C#
    /// identifiers don't allow.</summary>
    private static string SanitizeIdentifier(string name)
    {
        var chars = name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray();
        var result = new string(chars);
        if (result.Length == 0) return "Component";
        return char.IsDigit(result[0]) ? "_" + result : result;
    }

    /// <summary>Evidence-based, not a fact lookup -- see NullabilityInference's own doc comment
    /// for the DerivedColumn half. Shared by the single-destination path and
    /// <see cref="GenerateConditionalSplitFlow"/> (a Conditional Split's own branches can
    /// reference a Data Conversion column exactly like a plain destination flow can -- e.g.
    /// RBC_Demo_ETL's CSPLIT_Validity referencing CustomerID_i4 -- so both paths need the same
    /// set). A Data Conversion output column is nullable BY CONSTRUCTION, not by ISNULL evidence
    /// the way NullabilityInference's other entries are -- IgnoreFailure (the only disposition
    /// this tool generates against, see DataConvertPayload's own doc comment) can always null
    /// out the value, regardless of whether anything downstream ever guards it. EntityEmitter/
    /// TransformEmitter/RouterEmitter all key off this same set by the destination's/split's own
    /// pipeline column name, so computing it once here is sufficient for all three.</summary>
    private static HashSet<string> ResolveNullableColumnNames(DataFlowPlan flow)
    {
        var nullableColumnNames = flow.DerivedColumn is null
            ? new HashSet<string>()
            : NullabilityInference.InferNullableColumnNames([flow.DerivedColumn]);

        if (flow.DataConversion?.DataConvert is { } dataConvert)
        {
            foreach (var col in dataConvert.Columns)
                nullableColumnNames.Add(col.OutputColumnName);

            // The RAW source column feeding each conversion is just as capable of being NULL in
            // the real data (confirmed empirically -- see DataConvertPayload's own doc comment,
            // row ID=5's NULL source columns). SqlRowReaderEmitter's own IsDBNull guard already
            // applies to a nullable-inferred STRING column (unlike EntityEmitter/SqlRowEmitter's
            // "string never needs a type change" exclusion -- see that emitter's own comment) --
            // it just needs these names in the same set. Without this, GetFieldValue<string>
            // throws SqlNullValueException on a real NULL row, caught by actually running the
            // generated code against seeded NULL data, not assumed.
            var flowLineage = LineageBuilder.Build(flow.Pipeline);
            foreach (var col in dataConvert.Columns)
            {
                var sourceEdge = flowLineage.Edges.FirstOrDefault(e => e.Kind == "DataConversion" && e.ToColumnName == col.OutputColumnName);
                if (sourceEdge is not null) nullableColumnNames.Add(sourceEdge.FromColumnName);
            }
        }

        // A string-source/int-destination passthrough column (SsisFn.ParseWstrToI4, 2026-08-30)
        // is nullable BY CONSTRUCTION, the same reasoning as a Data Conversion column above --
        // ParseWstrToI4 always returns int? (string and string? are the same runtime type, so
        // there is no non-nullable overload the way NarrowR8ToI4's double/double? pair has), so
        // the destination entity property must already be nullable by the time EntityEmitter
        // runs, not just inside TransformEmitter's own later mismatch detection.
        foreach (var col in TransformEmitter.DetectParsedWstrToI4Columns(flow.DestinationComponent))
            nullableColumnNames.Add(col);

        return nullableColumnNames;
    }

    /// <summary>Resolves a flow's source (Flat File or OLE DB) into a row-type file pair plus a
    /// <see cref="FlowSourceSpec"/> Program.cs can wire up -- shared by both the single-
    /// destination path and <see cref="GenerateConditionalSplitFlow"/>, since a Conditional
    /// Split reads from exactly the same kind of source a single-destination flow does; only
    /// what happens to each row downstream differs. <paramref name="rowTypeBaseName"/> is
    /// "{entityName}" for a single-destination flow and a sanitized flow/task name for a split
    /// (which has no single destination entity to key it off). <paramref name="destinationForSqlCheck"/>
    /// is the destination BuildSqlFlowSource compares an OLE DB Source's server/database
    /// against -- the flow's own destination, or a split's default branch's destination. Null
    /// when the flow has no SQL destination at all (an OLE DB Command flow, or a Multicast whose
    /// only branches are Flat File Destinations) -- BuildSqlFlowSource skips the check entirely
    /// in that case, the same reasoning already established for a Flat File Destination.</summary>
    private static (string RowTypeName, FlowSourceSpec Source)? ResolveFlowSource(
        PackageSpec package, string ns, string rowTypeBaseName, DataFlowPlan flow, PipelineComponentSpec? destinationForSqlCheck,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files, List<GenerationGap> gaps,
        IReadOnlySet<string>? nullableColumnNames = null)
    {
        if (flow.FlatFileSource is { } flatFileSource && flatFileSource.FlatFileSource?.ConnectionName is { } csvCmName
            && FindConnectionManager(package, csvCmName) is { } csvConnectionManager)
        {
            var rowTypeName = rowTypeBaseName + "CsvRow";
            var fileSourceKey = rowTypeBaseName;

            Merge(files, gaps, CsvRowEmitter.Emit($"{ns}.Csv", rowTypeName, csvConnectionManager));
            Merge(files, gaps, ClassMapEmitter.Emit($"{ns}.Csv", rowTypeName, csvConnectionManager));
            fileSourceEntries.Add(BuildFileSourceEntry(fileSourceKey, csvConnectionManager, gaps));

            return (rowTypeName, new CsvFlowSource(flatFileSource.Name, fileSourceKey));
        }

        if (flow.OleDbSource is { } oleDbSource)
        {
            var sqlSource = BuildSqlFlowSource(package, flow.TaskName, oleDbSource, destinationForSqlCheck, gaps);
            if (sqlSource is null) return null; // BuildSqlFlowSource already added the reason

            var rowTypeName = rowTypeBaseName + "SqlRow";
            Merge(files, gaps, SqlRowEmitter.Emit($"{ns}.Sql", rowTypeName, oleDbSource, nullableColumnNames));
            Merge(files, gaps, SqlRowReaderEmitter.Emit($"{ns}.Sql", rowTypeName, oleDbSource, nullableColumnNames));

            return (rowTypeName, sqlSource);
        }

        if (flow.ExcelSource is { } excelSource)
        {
            var excelFlowSource = BuildExcelFlowSource(package, flow.TaskName, excelSource, fileSourceEntries, gaps);
            if (excelFlowSource is null) return null; // BuildExcelFlowSource already added the reason

            var rowTypeName = rowTypeBaseName + "ExcelRow";
            Merge(files, gaps, ExcelRowEmitter.Emit($"{ns}.Excel", rowTypeName, excelSource));
            Merge(files, gaps, ExcelRowReaderEmitter.Emit($"{ns}.Excel", rowTypeName, excelSource));

            return (rowTypeName, excelFlowSource);
        }

        gaps.Add(new GenerationGap(flow.TaskName, "no Flat File Source, OLE DB Source, ADO NET Source, or Excel Source (with a resolvable connection manager) found -- only [Flat File Source|OLE DB Source|ADO NET Source|Excel Source] -> [Derived Column] -> [OLE DB Destination|ADO NET Destination] is supported yet"));
        return null;
    }

    /// <summary>Same per-side resolution as <see cref="ResolveFlowSource"/>'s own two branches,
    /// just keyed off a raw source <see cref="PipelineComponentSpec"/> directly (a Merge Join's
    /// own two sides are never stored on a <see cref="DataFlowPlan"/> itself, since a
    /// single-destination flow only ever has one).</summary>
    private static (string RowTypeName, FlowSourceSpec Source)? ResolveMergeJoinSideSource(
        PackageSpec package, string ns, string rowTypeBaseName, PipelineComponentSpec sourceComponent,
        PipelineComponentSpec destinationForSqlCheck, List<FileSourceEntryRequest> fileSourceEntries,
        List<GeneratedFile> files, List<GenerationGap> gaps)
    {
        if (sourceComponent.ComponentClassId == "Microsoft.FlatFileSource" && sourceComponent.FlatFileSource?.ConnectionName is { } csvCmName
            && FindConnectionManager(package, csvCmName) is { } csvConnectionManager)
        {
            var rowTypeName = rowTypeBaseName + "CsvRow";
            var fileSourceKey = rowTypeBaseName;

            Merge(files, gaps, CsvRowEmitter.Emit($"{ns}.Csv", rowTypeName, csvConnectionManager));
            Merge(files, gaps, ClassMapEmitter.Emit($"{ns}.Csv", rowTypeName, csvConnectionManager));
            fileSourceEntries.Add(BuildFileSourceEntry(fileSourceKey, csvConnectionManager, gaps));

            return (rowTypeName, new CsvFlowSource(sourceComponent.Name, fileSourceKey));
        }

        if (SourceInfo.IsSqlSource(sourceComponent))
        {
            var sqlSource = BuildSqlFlowSource(package, sourceComponent.Name, sourceComponent, destinationForSqlCheck, gaps);
            if (sqlSource is null) return null; // BuildSqlFlowSource already added the reason

            var rowTypeName = rowTypeBaseName + "SqlRow";
            Merge(files, gaps, SqlRowEmitter.Emit($"{ns}.Sql", rowTypeName, sourceComponent));
            Merge(files, gaps, SqlRowReaderEmitter.Emit($"{ns}.Sql", rowTypeName, sourceComponent));

            return (rowTypeName, sqlSource);
        }

        gaps.Add(new GenerationGap(sourceComponent.Name, "no Flat File Source or SQL source (with a resolvable connection manager) found for this Merge Join side"));
        return null;
    }

    /// <summary>
    /// Generates a whole Merge Join flow: each side's own row type (via
    /// <see cref="ResolveMergeJoinSideSource"/>, the same emitters any single-source flow uses),
    /// the combined row type + mapper (<see cref="MergeJoinEmitter"/>), and the destination's own
    /// Model/Mapping/DbSet trio exactly like a plain single-destination flow -- a Merge Join's
    /// own combined row type is deliberately shaped so the destination's existing
    /// EntityEmitter/TransformEmitter code paths need no changes at all to consume it.
    /// </summary>
    private static ProgramFlowSpec? GenerateMergeJoinFlow(
        PackageSpec package, string ns, DataFlowPlan flow, MergeJoinPlan mergeJoin,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files,
        List<DbContextEmitter.TableSpec> tables, HashSet<string> functionsUsed, Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination,
        List<GenerationGap> gaps, ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams)
    {
        var entityName = TryResolveEntityName(flow.TaskName, flow.DestinationComponent, gaps);
        if (entityName is null) return null;

        if (!DestinationInfo.IsFastLoadConfigured(flow.DestinationComponent))
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"destination '{flow.DestinationComponent.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
            return null;
        }

        var baseName = SanitizeIdentifier(mergeJoin.Component.Name);
        var leftResult = ResolveMergeJoinSideSource(package, ns, baseName + "Left", mergeJoin.Left.SourceComponent, flow.DestinationComponent, fileSourceEntries, files, gaps);
        if (leftResult is null) return null;
        var rightResult = ResolveMergeJoinSideSource(package, ns, baseName + "Right", mergeJoin.Right.SourceComponent, flow.DestinationComponent, fileSourceEntries, files, gaps);
        if (rightResult is null) return null;
        var (leftRowTypeName, leftSource) = leftResult.Value;
        var (rightRowTypeName, rightSource) = rightResult.Value;
        var leftRowTypeNamespace = leftSource is CsvFlowSource ? $"{ns}.Csv" : $"{ns}.Sql";
        var rightRowTypeNamespace = rightSource is CsvFlowSource ? $"{ns}.Csv" : $"{ns}.Sql";

        var rowTypeName = baseName + "Row";
        var mapperClassName = baseName + "Mapper";
        var mergeJoinResult = MergeJoinEmitter.Emit(
            $"{ns}.Mapping", $"{ns}.Sql", $"{ns}.Ssis", rowTypeName, mapperClassName,
            leftRowTypeNamespace, leftRowTypeName, rightRowTypeNamespace, rightRowTypeName, mergeJoin);
        files.AddRange(mergeJoinResult.Files);
        gaps.AddRange(mergeJoinResult.Gaps);
        if (mergeJoinResult.Files.Count == 0) return null; // MergeJoinEmitter already recorded why

        Merge(files, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, flow.DestinationComponent, mergeJoinResult.NullableColumnNames));

        var transformClassName = entityName + "Transform";
        var transformResult = TransformEmitter.Emit(new TransformRequest(
            EmitSeams: emitSeams,
            MappingNamespace: $"{ns}.Mapping",
            TransformClassName: transformClassName,
            RowTypeNamespace: $"{ns}.Sql",
            RowTypeName: rowTypeName,
            EntityNamespace: $"{ns}.Model",
            EntityName: entityName,
            SsisFnNamespace: $"{ns}.Ssis",
            Pipeline: flow.Pipeline,
            DerivedColumns: [],
            DestinationComponent: flow.DestinationComponent,
            NullableColumnNames: mergeJoinResult.NullableColumnNames,
            // MergeJoinEmitter already resolved every one of rowTypeName's own columns itself
            // (including through a Data Conversion, via its own SourceColumnLineageId walk) --
            // the unrecognized-producer gate has no visibility into that resolution, so it must
            // be skipped here (see TransformRequest.TrustAllPassthroughColumns's own doc comment).
            TrustAllPassthroughColumns: true));
        Merge(files, gaps, transformResult.Result);
        foreach (var fn in transformResult.SsisFunctionsUsed) functionsUsed.Add(fn);
        foreach (var fn in mergeJoinResult.SsisFunctionsUsed) functionsUsed.Add(fn);

        if (transformResult.Result.Files.Count == 0) return null; // TransformEmitter already recorded why

        primaryKeysByDestination.TryGetValue(flow.DestinationComponent.RefId, out var primaryKey);
        tables.Add(new DbContextEmitter.TableSpec(entityName, flow.DestinationComponent, primaryKey));

        var mergeJoinSource = new MergeJoinFlowSource(
            mergeJoin.Component.Name, leftSource, rightSource, leftRowTypeName, rightRowTypeName, mapperClassName, mergeJoinResult.KeyClrType);

        if (authMode is null && ResolveDatabaseAuth(package, flow.DestinationComponent) is { } auth)
            (authMode, userId, targetServer, targetDatabase) = auth;

        return new ProgramFlowSpec(flow.TaskName, mergeJoinSource, rowTypeName, entityName, transformClassName, new SqlFlowSink());
    }

    /// <summary>
    /// Generates a whole Conditional Split flow: one shared source (via <see cref="ResolveFlowSource"/>),
    /// the flow's own shared Derived Column (if any, unchanged from the single-destination path
    /// -- scope decision 1, CLAUDE.md), one router (<see cref="RouterEmitter"/>), and one
    /// Model/Mapping/DbSet trio per DISTINCT destination -- two or more branches can resolve to
    /// the SAME destination (a Union All remerge, see PackagePlanner.ResolveBranch), in which
    /// case they share one entity/table but still get their own Mapping/*Transform.cs, combining
    /// the flow's shared Derived Column with whatever per-branch Derived Column(s) that branch's
    /// own chain passed through (e.g. a "tag" applied after the split). Buffers every branch's
    /// generated files/table/functions-used locally and only commits them (plus the router's own
    /// file) to the real <paramref name="files"/>/<paramref name="tables"/>/
    /// <paramref name="functionsUsed"/> once EVERY branch and the router succeed -- scope
    /// decision 2 (no partial split generation). A flow's own source-row-type file(s) (emitted
    /// eagerly by ResolveFlowSource, before any branch is attempted) are the one exception: they
    /// describe the source, not any branch, so they're valid output regardless of whether a
    /// branch downstream ends up failing -- an orphaned-but-harmless Sql/Csv row-type file is not
    /// "half-wired" the way a partially-wired Program.cs would be.
    /// </summary>
    private static ProgramConditionalSplitStep? GenerateConditionalSplitFlow(
        PackageSpec package, string ns, DataFlowPlan flow, ConditionalSplitPlan split,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files,
        List<DbContextEmitter.TableSpec> tables, HashSet<string> functionsUsed,
        Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams)
    {
        var flowBaseName = SanitizeIdentifier(flow.TaskName);
        var defaultDestination = split.Branches[^1].Destination;

        // Computed before ResolveFlowSource (unlike the single-destination path's own ordering
        // note doesn't apply here since there's no DerivedColumn-null gate before it in THIS
        // path) -- CsvRowEmitter/SqlRowEmitter/SqlRowReaderEmitter all need it to mark a Data
        // Conversion's own raw source column nullable. Missing this was a real bug, caught only
        // by actually running the generated code against a seeded NULL row: the reader's
        // GetFieldValue<string> (no IsDBNull guard, since the column wasn't in the set) threw
        // SqlNullValueException on ID=2's NULL SignupDateText, not by a unit test.
        var nullableColumnNames = ResolveNullableColumnNames(flow);

        var sourceResult = ResolveFlowSource(package, ns, flowBaseName, flow, defaultDestination, fileSourceEntries, files, gaps, nullableColumnNames);
        if (sourceResult is null) return null;
        var (rowTypeName, programSource) = sourceResult.Value;
        var rowTypeNamespace = programSource is CsvFlowSource ? $"{ns}.Csv" : programSource is ExcelFlowSource ? $"{ns}.Excel" : $"{ns}.Sql";

        // A split flow with neither a shared upstream Derived Column, a shared upstream Data
        // Conversion, nor any per-branch Derived Column (see ConditionalSplitBranchPlan.
        // DerivedColumns) is a direct-copy-with-routing flow -- out of scope for the same reason
        // a plain direct-copy flow is (the single-destination path's own gate, right above this
        // method, widened for the identical reason in the Data Conversion round). Real evidenced
        // case this widening closes: RBC_Demo_ETL's own DFT_MergeSortedBranches has NO Derived
        // Column anywhere (Split -> Sort -> Merge on each branch, no per-branch transform), yet
        // its destination genuinely needs SsisFn.ToNullableI4(row.CustomerID) for one column --
        // TransformEmitter already resolves that correctly via flow.DataConversion's own
        // convertedByName lookup (passed in below regardless of DerivedColumns.Count), this gate
        // just needs to stop rejecting the flow before that resolution ever runs.
        if (flow.DerivedColumn is null && flow.DataConversion is null && split.Branches.All(b => b.DerivedColumns.Count == 0))
        {
            gaps.Add(new GenerationGap(flow.TaskName, "no Derived Column found in this Data Flow Task -- a direct-copy pipeline's transform is not generated yet, so this flow is not wired into Program.cs"));
            return null;
        }

        var routerClassName = SanitizeIdentifier(split.Component.Name) + "Router";
        var routerResult = RouterEmitter.Emit($"{ns}.Mapping", routerClassName, rowTypeNamespace, rowTypeName, $"{ns}.Ssis", split, flow.Pipeline, flow.DataConversion);
        if (routerResult.Result.Files.Count == 0)
        {
            gaps.AddRange(routerResult.Result.Gaps);
            return null;
        }

        // Phase A: validate every branch and resolve its entity name, without emitting
        // anything yet -- entityNameCounts (below) is what lets Phase B tell a genuine
        // convergence (two branches, same entity, via a shared destination) apart from the
        // ordinary one-branch-one-destination shape, and getting that right needs every
        // branch's name known up front.
        var entityNames = new List<string>();
        foreach (var branch in split.Branches)
        {
            if (!DestinationInfo.IsFastLoadConfigured(branch.Destination))
            {
                gaps.Add(new GenerationGap(flow.TaskName, $"Conditional Split branch '{branch.OutputName}': destination '{branch.Destination.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
                return null;
            }

            var entityName = TryResolveEntityName(flow.TaskName, branch.Destination, gaps);
            if (entityName is null) return null;
            entityNames.Add(entityName);
        }
        var entityNameCounts = entityNames
            .GroupBy(n => n, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var branchFiles = new List<GeneratedFile>();
        var branchTables = new List<DbContextEmitter.TableSpec>();
        var branchFunctionsUsed = new HashSet<string>();
        var branches = new List<ProgramConditionalSplitBranch>();
        var emittedDestinationRefIds = new HashSet<string>(StringComparer.Ordinal);
        (string AuthMode, string? UserId, string? Server, string? Database)? resolvedAuth = null;

        for (var i = 0; i < split.Branches.Count; i++)
        {
            var branch = split.Branches[i];
            var entityName = entityNames[i];

            // Two or more branches sharing an entity (a Union All remerge) still need distinct
            // transform class names -- every other shape keeps today's plain "{Entity}Transform"
            // unchanged, so this never renames an already-generated single-branch-per-destination
            // fixture's output.
            var branchTransformClassName = entityNameCounts[entityName] > 1
                ? entityName + SanitizeIdentifier(branch.OutputName) + "Transform"
                : entityName + "Transform";

            if (emittedDestinationRefIds.Add(branch.Destination.RefId))
            {
                Merge(branchFiles, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, branch.Destination, nullableColumnNames));
                primaryKeysByDestination.TryGetValue(branch.Destination.RefId, out var primaryKey);
                branchTables.Add(new DbContextEmitter.TableSpec(entityName, branch.Destination, primaryKey));
            }

            var derivedColumns = new List<PipelineComponentSpec>();
            if (flow.DerivedColumn is not null) derivedColumns.Add(flow.DerivedColumn);
            derivedColumns.AddRange(branch.DerivedColumns);

            var transformResult = TransformEmitter.Emit(new TransformRequest(
                EmitSeams: emitSeams,
                MappingNamespace: $"{ns}.Mapping",
                TransformClassName: branchTransformClassName,
                RowTypeNamespace: rowTypeNamespace,
                RowTypeName: rowTypeName,
                EntityNamespace: $"{ns}.Model",
                EntityName: entityName,
                SsisFnNamespace: $"{ns}.Ssis",
                Pipeline: flow.Pipeline,
                DerivedColumns: derivedColumns,
                DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
                DestinationComponent: branch.Destination,
                NullableColumnNames: nullableColumnNames));
            Merge(branchFiles, gaps, transformResult.Result);
            foreach (var fn in transformResult.SsisFunctionsUsed) branchFunctionsUsed.Add(fn);

            if (transformResult.Result.Files.Count == 0) return null; // TransformEmitter already recorded why

            branches.Add(new ProgramConditionalSplitBranch(branch.OutputName, entityName, branchTransformClassName));

            if (resolvedAuth is null && ResolveDatabaseAuth(package, branch.Destination) is { } auth)
                resolvedAuth = auth;
        }

        // Every branch and the router succeeded -- commit the buffered output for real.
        files.AddRange(branchFiles);
        tables.AddRange(branchTables);
        foreach (var fn in branchFunctionsUsed) functionsUsed.Add(fn);
        foreach (var fn in routerResult.SsisFunctionsUsed) functionsUsed.Add(fn);
        files.AddRange(routerResult.Result.Files);

        if (authMode is null && resolvedAuth is { } resolved)
            (authMode, userId, targetServer, targetDatabase) = resolved;

        return new ProgramConditionalSplitStep(flow.TaskName, programSource, rowTypeName, routerClassName, branches);
    }

    /// <summary>Mirrors <see cref="GenerateConditionalSplitFlow"/>'s own Phase A/B shape (resolve
    /// every branch's entity name up front, then emit, committing only if everything succeeds) --
    /// the two real differences are that there's no RouterEmitter call at all (Multicast has no
    /// condition to translate) and each branch is independently checked for whether it targets a
    /// Flat File Destination (the real evidenced shape, RBC_Demo_ETL's own DFT_FixedWidthImport,
    /// fans to BOTH an OLE DB Destination and a Flat File Destination from one Multicast -- no
    /// evidenced Conditional Split ever needed this, so that method's own branches are still
    /// assumed uniformly SQL-sunk).</summary>
    private static ProgramMulticastStep? GenerateMulticastFlow(
        PackageSpec package, string ns, DataFlowPlan flow, MulticastPlan multicast,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files,
        List<DbContextEmitter.TableSpec> tables, HashSet<string> functionsUsed,
        Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams)
    {
        var flowBaseName = SanitizeIdentifier(flow.TaskName);
        var defaultDestination = multicast.Branches[^1].Destination;
        var defaultIsFlatFile = defaultDestination.ComponentClassId == "Microsoft.FlatFileDestination";

        var nullableColumnNames = ResolveNullableColumnNames(flow);

        // A null destinationForSqlCheck is passed when every branch is a Flat File Destination --
        // there is no SQL destination anywhere in this flow to compare the source's connection
        // against, the same reasoning BuildSqlFlowSource's own null-destinationComponent case
        // documents for an OLE DB Command flow.
        var sourceResult = ResolveFlowSource(package, ns, flowBaseName, flow,
            defaultIsFlatFile ? null : defaultDestination, fileSourceEntries, files, gaps, nullableColumnNames);
        if (sourceResult is null) return null;
        var (rowTypeName, programSource) = sourceResult.Value;
        var rowTypeNamespace = programSource is CsvFlowSource ? $"{ns}.Csv" : programSource is ExcelFlowSource ? $"{ns}.Excel" : $"{ns}.Sql";

        // Phase A: validate every branch and resolve its entity name, without emitting anything
        // yet -- same convergence-detection shape as GenerateConditionalSplitFlow's own Phase A.
        var entityNames = new List<string>();
        var branchIsFlatFile = new List<bool>();
        foreach (var branch in multicast.Branches)
        {
            var isFlatFile = branch.Destination.ComponentClassId == "Microsoft.FlatFileDestination";
            branchIsFlatFile.Add(isFlatFile);

            if (!isFlatFile && !DestinationInfo.IsFastLoadConfigured(branch.Destination))
            {
                gaps.Add(new GenerationGap(flow.TaskName, $"Multicast branch '{branch.OutputName}': destination '{branch.Destination.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
                return null;
            }

            // Keyed off the DESTINATION component's own name, not the branch's output name --
            // consistent with the SQL branch's own convention (entityName comes from the
            // destination table, never the branch) so two branches that converge to the SAME
            // Flat File Destination (via a Union All) naturally compute the identical entity
            // name too, the same convergence-detection shape entityNameCounts below relies on.
            var entityName = isFlatFile
                ? SanitizeIdentifier(flow.TaskName) + SanitizeIdentifier(branch.Destination.Name)
                : TryResolveEntityName(flow.TaskName, branch.Destination, gaps);
            if (entityName is null) return null;
            entityNames.Add(entityName);
        }
        var entityNameCounts = entityNames
            .GroupBy(n => n, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var branchFiles = new List<GeneratedFile>();
        var branchTables = new List<DbContextEmitter.TableSpec>();
        var branchFunctionsUsed = new HashSet<string>();
        var branches = new List<ProgramMulticastBranch>();
        var emittedDestinationRefIds = new HashSet<string>(StringComparer.Ordinal);
        (string AuthMode, string? UserId, string? Server, string? Database)? resolvedAuth = null;

        for (var i = 0; i < multicast.Branches.Count; i++)
        {
            var branch = multicast.Branches[i];
            var entityName = entityNames[i];
            var isFlatFile = branchIsFlatFile[i];

            var branchTransformClassName = entityNameCounts[entityName] > 1
                ? entityName + SanitizeIdentifier(branch.OutputName) + "Transform"
                : entityName + "Transform";

            FlowSinkSpec? sink = null;
            if (emittedDestinationRefIds.Add(branch.Destination.RefId))
            {
                Merge(branchFiles, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, branch.Destination, nullableColumnNames));
                if (isFlatFile)
                {
                    var flatFileSink = ResolveFlatFileSink(package, flow.TaskName, branch.Destination, entityName, fileSourceEntries, gaps);
                    if (flatFileSink is null) return null; // ResolveFlatFileSink already added the reason
                    sink = flatFileSink;
                }
                else
                {
                    primaryKeysByDestination.TryGetValue(branch.Destination.RefId, out var primaryKey);
                    branchTables.Add(new DbContextEmitter.TableSpec(entityName, branch.Destination, primaryKey));
                    sink = new SqlFlowSink();
                }
            }

            var derivedColumns = new List<PipelineComponentSpec>();
            if (flow.DerivedColumn is not null) derivedColumns.Add(flow.DerivedColumn);
            derivedColumns.AddRange(branch.DerivedColumns);

            var transformResult = TransformEmitter.Emit(new TransformRequest(
                EmitSeams: emitSeams,
                MappingNamespace: $"{ns}.Mapping",
                TransformClassName: branchTransformClassName,
                RowTypeNamespace: rowTypeNamespace,
                RowTypeName: rowTypeName,
                EntityNamespace: $"{ns}.Model",
                EntityName: entityName,
                SsisFnNamespace: $"{ns}.Ssis",
                Pipeline: flow.Pipeline,
                DerivedColumns: derivedColumns,
                DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
                DestinationComponent: branch.Destination,
                NullableColumnNames: nullableColumnNames));
            Merge(branchFiles, gaps, transformResult.Result);
            foreach (var fn in transformResult.SsisFunctionsUsed) branchFunctionsUsed.Add(fn);

            if (transformResult.Result.Files.Count == 0) return null; // TransformEmitter already recorded why

            // sink is only null when this destination RefId was already emitted by an earlier
            // branch (a convergence) -- that earlier branch shares this one's own entityName (see
            // the naming comment above), so ProgramEmitter's own DistinctBy(EntityName) is what
            // actually picks which branch's Sink value gets used for the shared DI registration;
            // this placeholder is a type-correct stand-in for the branch record, never read for
            // real data.
            sink ??= isFlatFile
                ? new FlatFileFlowSink(entityName, false, null, [])
                : new SqlFlowSink();

            branches.Add(new ProgramMulticastBranch(branch.OutputName, entityName, branchTransformClassName, sink));

            if (!isFlatFile && resolvedAuth is null && ResolveDatabaseAuth(package, branch.Destination) is { } auth)
                resolvedAuth = auth;
        }

        // Every branch succeeded -- commit the buffered output for real.
        files.AddRange(branchFiles);
        tables.AddRange(branchTables);
        foreach (var fn in branchFunctionsUsed) functionsUsed.Add(fn);

        if (authMode is null && resolvedAuth is { } resolved)
            (authMode, userId, targetServer, targetDatabase) = resolved;

        return new ProgramMulticastStep(flow.TaskName, programSource, rowTypeName, branches);
    }

    /// <summary>Resolves an OLE DB Command flow -- no destination component, no EF entity, no
    /// IBulkSink; the command runs once per source row via Etl.Core.Pipeline.
    /// OleDbCommandStep&lt;TRow&gt;, on the same shared connection/transaction as everything else
    /// in the package (IUnitOfWork.ExecuteSqlAsync). Validates that the command's own connection
    /// manager targets the SAME server/database as the flow's own source -- there is no
    /// destination here to compare against, so this is the one remaining place a second,
    /// untracked connection could otherwise slip through unnoticed.</summary>
    private static ProgramOleDbCommandStep? GenerateOleDbCommandFlow(
        PackageSpec package, string ns, DataFlowPlan flow, OleDbCommandPlan commandPlan,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams)
    {
        var sourceComponent = flow.OleDbSource ?? flow.ExcelSource ?? flow.FlatFileSource;
        if (sourceComponent is null)
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"OLE DB Command '{commandPlan.Component.Name}' has no resolvable upstream source -- not supported"));
            return null;
        }

        var commandCmName = commandPlan.Component.OleDbCommand?.ConnectionName;
        var commandParsed = commandCmName is not null ? FindConnectionManager(package, commandCmName)?.Parsed : null;
        var sourceCmName = SourceInfo.ConnectionName(sourceComponent);
        var sourceParsed = sourceCmName is not null ? FindConnectionManager(package, sourceCmName)?.Parsed : null;
        if (commandParsed is null || sourceParsed is null || commandParsed.Server != sourceParsed.Server || commandParsed.Database != sourceParsed.Database)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"OLE DB Command '{commandPlan.Component.Name}' targets a different server/database than this flow's own source -- a second connection is not supported for a command-only flow"));
            return null;
        }

        var flowBaseName = SanitizeIdentifier(flow.TaskName);
        var sourceResult = ResolveFlowSource(package, ns, flowBaseName, flow, destinationForSqlCheck: null, fileSourceEntries, files, gaps);
        if (sourceResult is null) return null; // ResolveFlowSource already added the reason
        var (rowTypeName, programSource) = sourceResult.Value;

        // Every parameter must resolve to a real column on the resolved row type -- matched by
        // NAME (the command's own input column cachedName, which is itself cached FROM the
        // upstream source's own output column name at design time, confirmed real from the
        // evidenced XML) since there is no intervening transform between source and command in
        // any evidenced shape.
        var sourceOutput = sourceComponent.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (sourceOutput is null)
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"Source '{sourceComponent.Name}' has no non-error output -- not supported"));
            return null;
        }
        var rowColumnNames = PipelineResolver.Resolve(sourceOutput).Columns.Select(c => c.PipelineColumnName).ToHashSet(StringComparer.Ordinal);
        var unresolved = commandPlan.ParameterColumnNames.Where(p => !rowColumnNames.Contains(p)).ToList();
        if (unresolved.Count > 0)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"OLE DB Command '{commandPlan.Component.Name}' references column(s) not found on its own source's output ({string.Join(", ", unresolved)}) -- not supported"));
            return null;
        }

        // commandParsed is guaranteed non-null here -- the earlier server/database check already
        // required it to resolve (and to match the source's own connection).
        if (authMode is null)
        {
            (authMode, userId, targetServer, targetDatabase) = commandParsed!.AuthMode == "SqlLogin"
                ? ("SqlServer", commandParsed.UserId, commandParsed.Server, commandParsed.Database)
                : ("Windows", null, commandParsed.Server, commandParsed.Database);
        }

        return new ProgramOleDbCommandStep(flow.TaskName, programSource, rowTypeName, commandPlan.SqlTemplate, commandPlan.ParameterColumnNames);
    }

    /// <summary>
    /// Generates a Microsoft.Aggregate flow (GroupBy + Count) -- built speculatively 2026-08-30,
    /// see <see cref="PackagePlanner.AggregatePlan"/>'s own doc comment for why (the one real
    /// evidenced instance is unreachable, blocked by its own upstream Lookup regardless). The
    /// destination's own row type is the AGGREGATE's own output shape
    /// (<see cref="AggregateRowEmitter"/>, NOT the flow's raw source row -- an Aggregate's own
    /// output has no external metadata columns at all, the same reason <c>MergeJoinEmitter</c>
    /// resolves its own combined row type straight off the raw output column rather than through
    /// <see cref="PipelineResolver"/>), fed at runtime by <c>Etl.Core.Data.AggregateRowSource</c>
    /// wrapping the flow's own true SQL source. Scoped deliberately narrow: only a SQL-sourced
    /// flow (OLE DB/ADO NET Source) is supported -- a Flat File/Excel-sourced Aggregate is
    /// unevidenced and out of scope for this already-speculative round.
    /// </summary>
    /// <summary>
    /// Derives a Lookup's join key from the package itself: SSIS records it on the joining INPUT
    /// column, as a <c>JoinToReferenceColumn</c> custom property naming the reference-table column
    /// it matches against.
    ///
    /// <b>This corrects a long-standing wrong conclusion in this project</b> -- that "SSIS does not
    /// persist a Lookup's join key anywhere in the saved .dtsx". That was concluded from
    /// <c>SyntheticLookupSplit.dtsx</c>, a fixture built through the object model whose Lookup
    /// input columns were never mapped at all, so it genuinely had no join key to persist; absence
    /// in that one fixture was read as absence in the format. A real SSDT-authored Lookup
    /// (RBC_Demo_ETL's own <c>LKP_Country</c>: input <c>Country</c> -> reference
    /// <c>CountryName</c>) persists it, and the extractor was already capturing it generically via
    /// <c>PipelineInputColumnSpec.Properties</c> -- no reader change was needed to read it back.
    ///
    /// Returns null when no input column declares one, which is a real reachable case (that same
    /// synthetic fixture) -- the caller then falls back to a human-confirmed decision rather than
    /// guessing.
    /// </summary>
    private static (string InputColumn, string ReferenceColumn)? TryDeriveLookupJoinKey(PipelineComponentSpec lookup)
    {
        foreach (var column in lookup.Inputs.SelectMany(i => i.Columns))
        {
            var joinTo = column.Properties.FirstOrDefault(p => p.Name == "JoinToReferenceColumn")?.Value;
            if (!string.IsNullOrWhiteSpace(joinTo)) return (column.CachedName, joinTo);
        }
        return null;
    }

    /// <summary>
    /// Generates a full-cache Lookup flow from a resolved join key -- derived from the package by
    /// <see cref="TryDeriveLookupJoinKey"/>, or (only when the package genuinely declares none)
    /// human-confirmed via <see cref="GapDecisionSpec"/>. Everything else about a Lookup is in the
    /// saved .dtsx -- the reference query, the reference columns, and each output column's own
    /// <c>CopyFromReferenceColumn</c> -- so no part of the generated output is guessed.
    ///
    /// Deliberately narrow, matching the one shape that can be verified end to end today: a
    /// single-destination SQL flow whose Lookup routes exactly one output onward. A Lookup that
    /// also routes its no-match rows somewhere (RBC_Demo_ETL's own DFT_LookupAndAggregate does,
    /// into a Multicast feeding an Aggregate) is a genuinely different, composed shape and is
    /// gapped rather than half-generated.
    /// </summary>
    private static ProgramFlowSpec? GenerateLookupFlow(
        PackageSpec package, string ns, DataFlowPlan flow, PipelineComponentSpec lookup, string cacheClassName,
        (string InputColumn, string ReferenceColumn) joinKey,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files, List<DbContextEmitter.TableSpec> tables,
        HashSet<string> functionsUsed, Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams)
    {
        if (flow.DestinationComponent.ComponentClassId == "Microsoft.FlatFileDestination")
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"Lookup '{lookup.Name}' feeds a Flat File Destination -- only a SQL destination is supported for a Lookup flow yet"));
            return null;
        }

        if (!DestinationInfo.IsFastLoadConfigured(flow.DestinationComponent))
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"destination '{flow.DestinationComponent.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
            return null;
        }

        // Exactly one non-error output may carry rows onward. Two means the no-match rows go
        // somewhere too, which needs a second wired branch this shape does not generate.
        var routedOutputs = lookup.Outputs
            .Where(o => o.IsErrorOut != true && flow.Pipeline.Paths.Any(p => p.StartId == o.RefId))
            .ToList();
        if (routedOutputs.Count != 1)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"Lookup '{lookup.Name}' routes {routedOutputs.Count} outputs onward ({string.Join(", ", routedOutputs.Select(o => $"'{o.Name}'"))}) -- only a single routed output (match rows straight to one destination) is supported yet; a wired no-match branch is a composed shape this tool does not generate"));
            return null;
        }

        var matchOutput = routedOutputs[0];

        // The join key column has to exist on the Lookup's own input, or the confirmed decision
        // does not describe this package -- a stale decision reaching generation is exactly what
        // must fail loudly rather than produce code referencing a column that isn't there.
        var inputColumn = lookup.Inputs.SelectMany(i => i.Columns)
            .FirstOrDefault(c => c.CachedName == joinKey.InputColumn);
        if (inputColumn is null)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"the confirmed join key names input column '{joinKey.InputColumn}', which Lookup '{lookup.Name}' does not have (its input columns are: {string.Join(", ", lookup.Inputs.SelectMany(i => i.Columns).Select(c => $"'{c.CachedName}'"))}) -- re-review the decision against the current package"));
            return null;
        }

        var keyType = SsisPipelineTypeMap.Resolve(inputColumn.CachedDataType);
        if (keyType is null)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"join key column '{joinKey.InputColumn}' has unmapped pipeline data type '{inputColumn.CachedDataType}'"));
            return null;
        }

        if (lookup.Lookup?.ReferenceColumns.Any(c => c.Name == joinKey.ReferenceColumn) != true)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"the confirmed join key names reference column '{joinKey.ReferenceColumn}', which is not in Lookup '{lookup.Name}''s own reference query result set -- re-review the decision against the current package"));
            return null;
        }

        // Each added column's source reference column IS persisted, on the output column's own
        // CopyFromReferenceColumn custom property -- so only the join key ever needed a human.
        var outputToReference = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in matchOutput.Columns)
        {
            var copyFrom = column.Properties.FirstOrDefault(p => p.Name == "CopyFromReferenceColumn")?.Value;
            if (!string.IsNullOrWhiteSpace(copyFrom)) outputToReference[column.Name] = copyFrom;
        }

        var entityName = TryResolveEntityName(flow.TaskName, flow.DestinationComponent, gaps);
        if (entityName is null) return null;

        var nullableColumnNames = ResolveNullableColumnNames(flow);
        var sourceResult = ResolveFlowSource(package, ns, entityName, flow, flow.DestinationComponent, fileSourceEntries, files, gaps, nullableColumnNames);
        if (sourceResult is null) return null; // ResolveFlowSource already added the reason
        var (rowTypeName, programSource) = sourceResult.Value;

        Merge(files, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, flow.DestinationComponent, nullableColumnNames));

        var transformClassName = entityName + "Transform";
        var transformResult = TransformEmitter.Emit(new TransformRequest(
            EmitSeams: emitSeams,
            MappingNamespace: $"{ns}.Mapping",
            TransformClassName: transformClassName,
            RowTypeNamespace: rowTypeName.EndsWith("CsvRow", StringComparison.Ordinal) ? $"{ns}.Csv" : $"{ns}.Sql",
            RowTypeName: rowTypeName,
            EntityNamespace: $"{ns}.Model",
            EntityName: entityName,
            SsisFnNamespace: $"{ns}.Ssis",
            Pipeline: flow.Pipeline,
            DerivedColumns: flow.DerivedColumn is null ? [] : [flow.DerivedColumn],
            DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
            DestinationComponent: flow.DestinationComponent,
            NullableColumnNames: nullableColumnNames,
            LookupJoin: new LookupJoinSpec(cacheClassName, "lookup", joinKey.InputColumn, keyType.ClrTypeName, outputToReference)));
        Merge(files, gaps, transformResult.Result);
        foreach (var fn in transformResult.SsisFunctionsUsed) functionsUsed.Add(fn);

        if (transformResult.Result.Files.Count == 0) return null; // TransformEmitter already recorded why

        primaryKeysByDestination.TryGetValue(flow.DestinationComponent.RefId, out var primaryKey);
        tables.Add(new DbContextEmitter.TableSpec(entityName, flow.DestinationComponent, primaryKey));

        if (authMode is null && ResolveDatabaseAuth(package, flow.DestinationComponent) is { } auth)
            (authMode, userId, targetServer, targetDatabase) = auth;

        var preload = new LookupPreload(
            VariableName: LowerFirst(cacheClassName),
            CacheClassName: cacheClassName,
            KeyClrType: keyType.ClrTypeName,
            ReferenceKeyColumn: joinKey.ReferenceColumn);

        return new ProgramFlowSpec(flow.TaskName, programSource, rowTypeName, entityName, transformClassName, new SqlFlowSink(), preload);
    }

    private static string LowerFirst(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static ProgramFlowSpec? GenerateAggregateFlow(
        PackageSpec package, string ns, DataFlowPlan flow, AggregatePlan aggregate,
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files, List<DbContextEmitter.TableSpec> tables,
        HashSet<string> functionsUsed, Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams)
    {
        if (flow.DestinationComponent.ComponentClassId == "Microsoft.FlatFileDestination")
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"Aggregate '{aggregate.Component.Name}' feeds a Flat File Destination -- only a SQL destination is supported for an Aggregate flow yet"));
            return null;
        }

        if (!DestinationInfo.IsFastLoadConfigured(flow.DestinationComponent))
        {
            gaps.Add(new GenerationGap(flow.TaskName, $"destination '{flow.DestinationComponent.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
            return null;
        }

        if (flow.OleDbSource is not { } sourceComponent)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"Aggregate '{aggregate.Component.Name}': only a SQL-sourced flow (OLE DB/ADO NET Source) is supported -- a Flat File/Excel-sourced Aggregate flow is not evidenced and not supported yet"));
            return null;
        }

        var entityName = TryResolveEntityName(flow.TaskName, flow.DestinationComponent, gaps);
        if (entityName is null) return null;

        var sourceOutput = sourceComponent.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        var groupByType = sourceOutput is null
            ? null
            : PipelineResolver.Resolve(sourceOutput).Columns.FirstOrDefault(c => c.PipelineColumnName == aggregate.GroupBySourceColumnName)?.Type;
        if (groupByType is null)
        {
            gaps.Add(new GenerationGap(flow.TaskName,
                $"Aggregate '{aggregate.Component.Name}': GroupBy source column '{aggregate.GroupBySourceColumnName}' could not be resolved to a CLR type"));
            return null;
        }

        // The source row's own counted column(s) are nullable BY CONSTRUCTION -- SSIS's own
        // Count aggregation excludes NULL (measured via a real dtexec run, see
        // AggregateCountSpec's own doc comment), so the generated `row.X != null` check must
        // compile against a genuinely nullable property, not an inferred-non-nullable one.
        var sourceNullableColumnNames = new HashSet<string>(aggregate.Counts.Select(c => c.SourceColumnName));

        var sourceRowBaseName = SanitizeIdentifier(aggregate.Component.Name) + "Source";
        var sourceResult = ResolveFlowSource(package, ns, sourceRowBaseName, flow, flow.DestinationComponent, fileSourceEntries, files, gaps, sourceNullableColumnNames);
        if (sourceResult is null) return null; // ResolveFlowSource already added the reason
        var (sourceRowTypeName, innerSource) = sourceResult.Value;

        var aggregateRowTypeName = entityName + "AggregateRow";
        Merge(files, gaps, AggregateRowEmitter.Emit($"{ns}.Sql", aggregateRowTypeName, aggregate.Component));

        var counts = aggregate.Counts.Select(c => new AggregateCountFieldSpec(c.OutputColumnName, c.SourceColumnName)).ToList();
        var aggregateSource = new AggregateFlowSource(aggregate.Component.Name, innerSource, sourceRowTypeName,
            aggregate.GroupByOutputColumnName, groupByType.ClrTypeName, counts);

        Merge(files, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, flow.DestinationComponent));

        var transformClassName = entityName + "Transform";
        var transformResult = TransformEmitter.Emit(new TransformRequest(
            EmitSeams: emitSeams,
            MappingNamespace: $"{ns}.Mapping",
            TransformClassName: transformClassName,
            RowTypeNamespace: $"{ns}.Sql",
            RowTypeName: aggregateRowTypeName,
            EntityNamespace: $"{ns}.Model",
            EntityName: entityName,
            SsisFnNamespace: $"{ns}.Ssis",
            Pipeline: flow.Pipeline,
            DerivedColumns: [],
            DataConversions: [],
            DestinationComponent: flow.DestinationComponent,
            // AggregateRowEmitter resolves aggregateRowTypeName's own properties independently
            // (straight off the Aggregate component's own output columns, not through pipeline
            // lineage) -- the same reasoning GenerateMergeJoinFlow's own call already
            // established for its own independently-resolved combined row type.
            TrustAllPassthroughColumns: true));
        Merge(files, gaps, transformResult.Result);
        foreach (var fn in transformResult.SsisFunctionsUsed) functionsUsed.Add(fn);

        if (transformResult.Result.Files.Count == 0) return null; // TransformEmitter already recorded why

        primaryKeysByDestination.TryGetValue(flow.DestinationComponent.RefId, out var primaryKey);
        tables.Add(new DbContextEmitter.TableSpec(entityName, flow.DestinationComponent, primaryKey));

        if (authMode is null && ResolveDatabaseAuth(package, flow.DestinationComponent) is { } auth)
            (authMode, userId, targetServer, targetDatabase) = auth;

        return new ProgramFlowSpec(flow.TaskName, aggregateSource, aggregateRowTypeName, entityName, transformClassName, new SqlFlowSink());
    }

    /// <summary>Resolves an OLE DB or ADO NET Source into a SqlFlowSource -- dispatches to
    /// <see cref="BuildOleDbFlowSource"/> or <see cref="BuildAdoNetFlowSource"/> after the one
    /// check both share: a resolvable connection manager targeting the same server/database as
    /// the flow's own destination (a second source connection is not supported yet).</summary>
    private static SqlFlowSource? BuildSqlFlowSource(
        PackageSpec package, string taskName, PipelineComponentSpec source,
        PipelineComponentSpec? destinationComponent, List<GenerationGap> gaps)
    {
        var sourceCmName = SourceInfo.ConnectionName(source);
        if (sourceCmName is not { } cmName || FindConnectionManager(package, cmName) is not { } sourceCm
            || sourceCm.Parsed is not { } sourceParsed)
        {
            gaps.Add(new GenerationGap(taskName, $"Source '{source.Name}' has no resolvable connection manager"));
            return null;
        }

        // A Flat File Destination has no server/database of its own to compare against at all --
        // its own connection is a file path, not a SQL connection -- so this check, which exists
        // to catch a SQL source/SQL destination pair that would silently need a second untracked
        // connection, doesn't apply: a Flat File Destination flow's SQL source is already the
        // only SQL connection this flow uses. A null destinationComponent (an OLE DB Command
        // flow, which has no destination component at all -- see OleDbCommandPlan's own doc
        // comment) is the same situation: the source's own connection is already the only SQL
        // connection this flow uses, so there's nothing to compare it against either.
        if (destinationComponent is not null && destinationComponent.ComponentClassId != "Microsoft.FlatFileDestination")
        {
            var destCmName = DestinationInfo.ConnectionName(destinationComponent);
            var destParsed = destCmName is not null ? FindConnectionManager(package, destCmName)?.Parsed : null;
            if (destParsed is null || sourceParsed.Server != destParsed.Server || sourceParsed.Database != destParsed.Database)
            {
                gaps.Add(new GenerationGap(taskName, $"Source '{source.Name}' targets a different server/database than this flow's own destination -- a separate source connection is not supported yet"));
                return null;
            }
        }

        return source.OleDbSource is { } oleDbPayload
            ? BuildOleDbFlowSource(taskName, source.Name, oleDbPayload, gaps)
            : BuildAdoNetFlowSource(taskName, source.Name, source.AdoNetSource!, gaps);
    }

    /// <summary>Builds the actual SELECT text per AccessMode: 0 (OpenRowset) builds
    /// <c>SELECT [Ext] AS [Comp], ... FROM {OpenRowset}</c> from ColumnMappings, guaranteeing
    /// every column resolves later by the pipeline's own buffer column name; 2 (SqlCommand)
    /// emits the author's own SQL verbatim (this tool never rewrites recorded SQL text) plus a
    /// non-fatal gap stating the assumption that the result set's own column names/aliases
    /// already match the buffer's column names -- a fact this tool cannot verify from the spec
    /// alone. Any other AccessMode is a fatal gap.</summary>
    private static SqlFlowSource? BuildOleDbFlowSource(string taskName, string sourceName, OleDbSourcePayload payload, List<GenerationGap> gaps)
    {
        switch (payload.AccessMode)
        {
            case 0:
                if (string.IsNullOrEmpty(payload.OpenRowset) || payload.ColumnMappings.Count == 0)
                {
                    gaps.Add(new GenerationGap(taskName, $"OLE DB Source '{sourceName}' has AccessMode=0 (OpenRowset) but no OpenRowset/ColumnMappings to build a SELECT from"));
                    return null;
                }
                var columns = string.Join(", ", payload.ColumnMappings.Select(m => $"[{m.ExternalColumnName}] AS [{m.ComponentColumnName}]"));
                return new SqlFlowSource(sourceName, $"SELECT {columns} FROM {payload.OpenRowset}");

            case 2:
                if (string.IsNullOrEmpty(payload.SqlCommand))
                {
                    gaps.Add(new GenerationGap(taskName, $"OLE DB Source '{sourceName}' has AccessMode=2 (SQL command) but no SqlCommand text"));
                    return null;
                }
                gaps.Add(new GenerationGap(taskName, $"OLE DB Source '{sourceName}' uses a SqlCommand -- generated code assumes each result-set column is named/aliased exactly like the pipeline's own buffer column name; verify before running. This tool never rewrites the author's own SQL.", IsBlocking: false));
                return new SqlFlowSource(sourceName, payload.SqlCommand);

            default:
                gaps.Add(new GenerationGap(taskName, $"OLE DB Source '{sourceName}' has AccessMode={(payload.AccessMode?.ToString() ?? "null")} -- only OpenRowset (0) and SQL command (2) are supported yet"));
                return null;
        }
    }

    /// <summary>Matches a bare "SELECT * FROM [SheetName$]" -- optionally without brackets, with
    /// a trailing semicolon -- and captures the sheet reference (including its own trailing
    /// "$"). This is deliberately the ONLY SqlCommand shape this tool ever translates for an
    /// Excel Source: <c>Etl.Core.Excel.ExcelRowSource</c> is an ExcelDataReader-backed grid
    /// reader with no query engine at all (see that type's own doc comment) -- it can select a
    /// worksheet by name, nothing more. A WHERE clause, column list, JOIN, or range address
    /// (e.g. "[Sheet1$A1:C10]") has no way to execute against that reader and is a named gap,
    /// never guessed.</summary>
    [GeneratedRegex(@"^SELECT\s+\*\s+FROM\s+\[?([^\[\]\s]+\$)\]?\s*;?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BareSelectStarFromSheetPattern();

    /// <summary>Resolves an Excel Source into an <see cref="ExcelFlowSource"/>. AccessMode=0
    /// (OpenRowset, i.e. a worksheet) is the one evidenced value across the tracked portfolio.
    /// AccessMode=2 (SqlCommand) -- built SPECULATIVELY 2026-08-30, no real package needs it --
    /// is accepted ONLY when the SQL text is a bare <see cref="BareSelectStarFromSheetPattern"/>
    /// match, functionally equivalent to naming the worksheet directly (confirmed via a raw
    /// System.Data.OleDb probe against the real checked-in workbook: the real ACE OLEDB provider
    /// returns the identical 9 rows/3 columns for "SELECT * FROM [DripEligibility$]" as it does
    /// for the bare worksheet OpenRowset) -- any other Jet/ACE SQL shape (see
    /// <see cref="ExcelSourcePayload"/>'s own doc comment) is a named gap, never guessed. The
    /// worksheet's own header-row setting (Extended Properties=HDR=YES/NO) lives on the EXCEL
    /// connection manager, not the component -- resolved from
    /// <see cref="ParsedConnectionString.Extras"/>, the generic pass-through capture every
    /// OLEDB-shaped connection string already gets for a segment with no bespoke field. That
    /// capture has a real, benign quirk here: "Extended Properties=&quot;EXCEL 12.0
    /// XML;HDR=YES&quot;" contains an UNQUOTED-parser-visible ';' inside its own quoted value, so
    /// the generic ';'-split (which has no quote awareness) lands "HDR" as its own extras key
    /// with a trailing stray '"' -- convenient, not a bug to fix, since it means HDR is directly
    /// readable without any Excel-specific parsing.</summary>
    private static ExcelFlowSource? BuildExcelFlowSource(
        PackageSpec package, string taskName, PipelineComponentSpec source,
        List<FileSourceEntryRequest> fileSourceEntries, List<GenerationGap> gaps)
    {
        var payload = source.ExcelSource!;

        string worksheetName;
        if (payload.AccessMode is null or 0)
        {
            if (string.IsNullOrEmpty(payload.OpenRowset))
            {
                gaps.Add(new GenerationGap(taskName, $"Excel Source '{source.Name}' has AccessMode=0 (OpenRowset) but no OpenRowset (worksheet name) to read from"));
                return null;
            }
            worksheetName = payload.OpenRowset;
        }
        else if (payload.AccessMode == 2)
        {
            var match = string.IsNullOrEmpty(payload.SqlCommand) ? null : BareSelectStarFromSheetPattern().Match(payload.SqlCommand.Trim());
            if (match is not { Success: true })
            {
                gaps.Add(new GenerationGap(taskName, $"Excel Source '{source.Name}' has AccessMode=2 (SqlCommand) with a query this tool cannot translate -- this generator's ExcelRowSource has no query engine, only a worksheet-name reader, so only a bare 'SELECT * FROM [SheetName$]' is supported; a WHERE clause, column list, JOIN, or range address is not."));
                return null;
            }
            worksheetName = match.Groups[1].Value;
        }
        else
        {
            gaps.Add(new GenerationGap(taskName, $"Excel Source '{source.Name}' has AccessMode={payload.AccessMode} -- only OpenRowset (0) or a bare 'SELECT * FROM [SheetName$]' SqlCommand (2) are supported yet"));
            return null;
        }

        var connectionManager = payload.ConnectionName is { } cmName ? FindConnectionManager(package, cmName) : null;
        if (connectionManager is null)
        {
            gaps.Add(new GenerationGap(taskName, $"Excel Source '{source.Name}' has no resolvable connection manager"));
            return null;
        }

        var fileSourceKey = source.Name;
        fileSourceEntries.Add(BuildFileSourceEntry(fileSourceKey, connectionManager, gaps));

        var hasHeaderRow = true; // the one evidenced real instance is HDR=YES; absent Extended Properties entirely, this is a reasonable fallback, not a fact this tool has confirmed
        var hdrEntry = connectionManager.Parsed?.Extras.FirstOrDefault(kv => kv.Key.Equals("HDR", StringComparison.OrdinalIgnoreCase));
        if (hdrEntry is { Value.Length: > 0 } found)
        {
            hasHeaderRow = found.Value.TrimEnd('"').Equals("YES", StringComparison.OrdinalIgnoreCase);
        }

        return new ExcelFlowSource(source.Name, fileSourceKey, worksheetName, hasHeaderRow);
    }

    /// <summary>ADO NET Source's own AccessMode enum is not reverse-engineered (see
    /// AdoNetSourcePayload's own doc comment for why) -- branches on which of
    /// <see cref="AdoNetSourcePayload.SqlCommand"/>/<see cref="AdoNetSourcePayload.TableOrViewName"/>
    /// is actually populated instead, sidestepping the need to know the enum's real mapping.
    /// Same SqlCommand-verbatim/non-fatal-gap and OpenRowset-column-list shapes as
    /// <see cref="BuildOleDbFlowSource"/>, just sourced from the ADO NET payload's own field
    /// names/quoting.</summary>
    private static SqlFlowSource? BuildAdoNetFlowSource(string taskName, string sourceName, AdoNetSourcePayload payload, List<GenerationGap> gaps)
    {
        if (!string.IsNullOrEmpty(payload.SqlCommand))
        {
            gaps.Add(new GenerationGap(taskName, $"ADO NET Source '{sourceName}' uses a SqlCommand -- generated code assumes each result-set column is named/aliased exactly like the pipeline's own buffer column name; verify before running. This tool never rewrites the author's own SQL.", IsBlocking: false));
            return new SqlFlowSource(sourceName, payload.SqlCommand);
        }

        var tableName = AdoNetSupport.NormalizeTableName(payload.TableOrViewName);
        if (!string.IsNullOrEmpty(tableName) && payload.ColumnMappings.Count > 0)
        {
            var columns = string.Join(", ", payload.ColumnMappings.Select(m => $"[{m.ExternalColumnName}] AS [{m.ComponentColumnName}]"));
            return new SqlFlowSource(sourceName, $"SELECT {columns} FROM {tableName}");
        }

        gaps.Add(new GenerationGap(taskName, $"ADO NET Source '{sourceName}' has neither a SqlCommand nor a TableOrViewName/ColumnMappings to build a SELECT from"));
        return null;
    }

    /// <summary>Design-time default file path, split into folder + file name for
    /// appsettings.json. Both real PoC packages show why this can't always be filled in: every
    /// CSV connection manager here is expression-driven off a project parameter (CLAUDE.md's
    /// own note), and only LoadEmployees' CM_EmployeesCsv happens to still carry a design-time
    /// default in ConnectionString for the Parsed.FilePath extraction to find. When there is
    /// none, a placeholder is emitted and the gap says exactly why, rather than guessing a
    /// path that was never in the package.</summary>
    private static FileSourceEntryRequest BuildFileSourceEntry(string key, ConnectionManagerSpec connectionManager, List<GenerationGap> gaps)
    {
        var filePath = connectionManager.Parsed?.FilePath;
        if (string.IsNullOrEmpty(filePath))
        {
            gaps.Add(new GenerationGap($"FileSource.{key}", $"connection manager '{connectionManager.ObjectName}' has no design-time default file path (likely expression-driven from a project parameter) -- SourceFolder/SourceFileName were generated as TODO placeholders; fill in appsettings.json manually"));
            return new FileSourceEntryRequest(key, "TODO", "TODO");
        }

        return new FileSourceEntryRequest(key, Path.GetDirectoryName(filePath) ?? "", Path.GetFileName(filePath));
    }

    /// <summary>
    /// Resolves a Flat File Destination's own file-format facts into a <see cref="FlatFileFlowSink"/>
    /// -- everything about the file's PHYSICAL layout (delimited vs fixed-width, delimiters,
    /// column widths, and the header row) lives on the flat file CONNECTION MANAGER
    /// (<see cref="FlatFileFormatSpec"/>, already modeled generically for the source side), not
    /// on the component itself; this only reads the component's own three bespoke properties
    /// (<see cref="FlatFileDestinationPayload.Overwrite"/>/.Header/.EscapeQualifier).
    ///
    /// Columns are emitted in the CONNECTION MANAGER's own column order (the real physical file
    /// layout), matched by name against the component's <see cref="PipelineColumnMappingSpec.ExternalColumnName"/>
    /// to find each column's PROPERTY name on the generated row/entity type -- not the mapping
    /// list's own declaration order, which isn't guaranteed to match the file's layout (though it
    /// does in both real evidenced instances).
    /// </summary>
    private static FlatFileFlowSink? ResolveFlatFileSink(
        PackageSpec package, string taskName, PipelineComponentSpec destination, string fileSourceKey,
        List<FileSourceEntryRequest> fileSourceEntries, List<GenerationGap> gaps)
    {
        var payload = destination.FlatFileDestination;
        var connectionManager = payload?.ConnectionName is { } cmName ? FindConnectionManager(package, cmName) : null;
        var format = connectionManager?.FlatFileFormat;
        if (payload is null || connectionManager is null || format is null)
        {
            gaps.Add(new GenerationGap(taskName, $"Flat File Destination '{destination.Name}' has no resolvable connection manager or file format -- not supported"));
            return null;
        }

        if (!string.IsNullOrEmpty(payload.Header))
        {
            gaps.Add(new GenerationGap(taskName, $"Flat File Destination '{destination.Name}' has a literal Header property set -- not supported yet, only the connection manager's own ColumnNamesInFirstDataRow header is generated"));
            return null;
        }

        // FixedWidth format with a generated header row is unevidenced -- neither real instance
        // (RBC_Demo_ETL's own CM_FF_ExportDelimited/CM_FF_ExportFixed) combines the two, and how
        // a header row's own column NAMES would be fixed-width-padded is not something this tool
        // has ground truth for. Named as a gap rather than guessed.
        if (format.Format == "FixedWidth" && format.ColumnNamesInFirstDataRow == true)
        {
            gaps.Add(new GenerationGap(taskName, $"Flat File Destination '{destination.Name}': a FixedWidth connection manager with ColumnNamesInFirstDataRow is not supported yet"));
            return null;
        }

        var componentColumnNameByExternalName = payload.ColumnMappings.ToDictionary(m => m.ExternalColumnName, m => m.ComponentColumnName);

        var columns = new List<FlatFileColumnFormatSpec>();
        foreach (var column in format.Columns)
        {
            if (!componentColumnNameByExternalName.TryGetValue(column.ObjectName, out var propertyName))
            {
                gaps.Add(new GenerationGap(taskName, $"Flat File Destination '{destination.Name}': connection manager column '{column.ObjectName}' has no mapped input column -- not supported"));
                return null;
            }

            // A fixed-width column is one the connection manager itself lays out by width with
            // no delimiter of its own (ColumnDelimiterDecoded empty) -- SSIS's own FixedWidth
            // format still encodes the ROW terminator as a synthetic trailing DELIMITED column
            // (see FlatFileDestinationPayload's own doc comment for the real evidenced example,
            // "RowEnd"), so this is a per-COLUMN decision, not a per-connection-manager one.
            var isFixedWidth = format.Format == "FixedWidth" && string.IsNullOrEmpty(column.ColumnDelimiterDecoded);
            columns.Add(new FlatFileColumnFormatSpec(propertyName, isFixedWidth ? column.MaximumWidth : null, column.ColumnDelimiterDecoded ?? ""));
        }

        // ColumnNamesInFirstDataRow is a source-and-destination-shared connection-manager
        // property (it tells a SOURCE to skip/parse row 1 as headers, and a DESTINATION to WRITE
        // row 1 as headers) -- confirmed real from CM_FF_ExportDelimited, the one evidenced
        // instance that sets it true. The header line uses the identical per-column-delimiter
        // algorithm as a data row, just with column NAMES instead of values (FixedWidth is
        // already rejected above, so every column here is plain delimited).
        string? headerLine = null;
        if (format.ColumnNamesInFirstDataRow == true)
        {
            var header = new System.Text.StringBuilder();
            foreach (var column in format.Columns)
                header.Append(column.ObjectName).Append(column.ColumnDelimiterDecoded);
            headerLine = header.ToString();
        }

        fileSourceEntries.Add(BuildFileSourceEntry(fileSourceKey, connectionManager, gaps));
        return new FlatFileFlowSink(fileSourceKey, payload.Overwrite == true, headerLine, columns);
    }

    /// <summary>"SqlLogin" (ConnectionManagerSpec.Parsed.AuthMode) -> SqlServer auth (user
    /// secrets, per ProjectEmitter); anything else -> Windows auth. Resolved from the
    /// DESTINATION's own connection manager only -- source CSV connection managers have no
    /// auth mode of their own to resolve. Also carries Server/Database along for the CLI's
    /// appsettings.Shared.json -- same connection manager, same call, no reason to re-look it
    /// up separately.</summary>
    private static (string AuthMode, string? UserId, string? Server, string? Database)? ResolveDatabaseAuth(PackageSpec package, PipelineComponentSpec destinationComponent)
    {
        var cmName = DestinationInfo.ConnectionName(destinationComponent);
        if (cmName is null || FindConnectionManager(package, cmName) is not { } cm || cm.Parsed is not { } parsed) return null;

        return parsed.AuthMode == "SqlLogin"
            ? ("SqlServer", parsed.UserId, parsed.Server, parsed.Database)
            : ("Windows", null, parsed.Server, parsed.Database);
    }

    /// <summary>Translates a <see cref="ForEachFileLoopPlan"/>'s own raw SSIS facts into
    /// everything <see cref="ProgramForEachFileLoopStep"/> needs: the per-iteration SQL
    /// expression (via <see cref="ForEachLoopEmitter"/>) and the resolved
    /// <c>Etl.Core.Abstractions.ForEachFileNameMode</c> member name. Either can fail
    /// independently, each with its own gap -- this step is simply omitted from the package
    /// (not fatal to the rest of it), matching every other single-step gap in this file.</summary>
    private static ProgramForEachFileLoopStep? ResolveForEachFileLoop(ForEachFileLoopPlan loop, List<GenerationGap> gaps)
    {
        const string currentFileParamName = "currentFile";
        var translated = ForEachLoopEmitter.TranslateSqlTemplate(loop.SqlTemplate, loop.VariableName, currentFileParamName);
        if (translated is NotTranslatable notTranslatable)
        {
            gaps.Add(new GenerationGap(loop.TaskName, $"{loop.InnerTaskName}: {notTranslatable.Reason}"));
            return null;
        }

        var nameMode = ResolveForEachFileNameMode(loop.NameRetrievalTypeRaw, loop.TaskName, gaps);
        if (nameMode is null) return null;

        return new ProgramForEachFileLoopStep(loop.TaskName, loop.Folder, loop.FileSpec, loop.Recurse, nameMode,
            currentFileParamName, ((TranslatedOk)translated).CSharpExpression);
    }

    /// <summary>Raw values from Microsoft's public IDTSForEachFileEnumerator documentation --
    /// see Etl.Core.Abstractions.ForEachFileNameMode's own doc comment for why this one enum
    /// mapping was not independently probed against a live object model the way most raw enums
    /// elsewhere in this tool are. Shared by both loop-body shapes (SQL and Data Flow) -- the
    /// enumerator's own config is identical regardless of what its body does with it.</summary>
    private static string? ResolveForEachFileNameMode(int? raw, string taskName, List<GenerationGap> gaps)
    {
        var nameMode = raw switch
        {
            null or 0 => "FullyQualified",
            1 => "NameAndExtension",
            2 => "NameOnly",
            _ => null,
        };
        if (nameMode is null)
        {
            gaps.Add(new GenerationGap(taskName,
                $"ForEach File Enumerator's own FileNameRetrievalType ({raw}) is not a recognized value -- not supported"));
        }
        return nameMode;
    }

    /// <summary>
    /// Generates a ForEach File Enumerator loop whose body is a Data Flow Task -- built
    /// speculatively 2026-08-30 (see <see cref="PackagePlanner.ForEachFileDataFlowPlan"/>'s own
    /// doc comment; zero real evidenced package anywhere in the tracked portfolio has this
    /// shape). Mirrors the ordinary single-destination flow's own generation (EntityEmitter/
    /// TransformEmitter/DbContextEmitter.TableSpec all resolve identically -- a per-iteration
    /// Data Flow Task is structurally the same flow, just re-run N times) with a deliberate
    /// narrowing, reported as its own named gap rather than attempted: the flow must be a plain
    /// single-destination SQL flow (Lookup/Conditional Split/Merge Join/Multicast/OLE DB
    /// Command/Flat File Destination inside a loop body would each need their own, separately
    /// unevidenced per-iteration semantics, so combining any of them with this already-
    /// speculative feature is out of scope). Skips ResolveFlowSource/BuildFileSourceEntry
    /// entirely -- unlike an ordinary flow, this source's file path is never a static
    /// appsettings.json value. The CSV row type/class map are still emitted via the SAME
    /// CsvRowEmitter/ClassMapEmitter an ordinary CSV flow uses (they only depend on the
    /// connection manager's own column FORMAT, never its path); the actual IRowSource
    /// construction happens fresh, per iteration, inside ProgramEmitter's own lambda around
    /// <see cref="PackagePlanner.ForEachFileDataFlowPlan.FilePathExpression"/>'s C# translation.
    /// </summary>
    private static ProgramForEachDataFlowLoopStep? GenerateForEachDataFlowLoop(
        PackageSpec package, string ns, ForEachFileDataFlowPlan loop,
        List<GeneratedFile> files, List<DbContextEmitter.TableSpec> tables, HashSet<string> functionsUsed,
        Dictionary<string, PrimaryKeyCandidateSpec> primaryKeysByDestination, List<GenerationGap> gaps,
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase,
        bool emitSeams)
    {
        var flow = loop.Flow;

        if (flow.Lookup is not null || flow.ConditionalSplit is not null || flow.MergeJoin is not null
            || flow.Multicast is not null || flow.OleDbCommand is not null)
        {
            gaps.Add(new GenerationGap(loop.TaskName,
                $"{flow.TaskName}: ForEach Loop's own Data Flow Task body uses a Lookup/Conditional Split/Merge Join/Multicast/OLE DB Command -- only a plain single-destination flow is supported inside a loop body yet"));
            return null;
        }

        if (flow.DestinationComponent.ComponentClassId == "Microsoft.FlatFileDestination")
        {
            gaps.Add(new GenerationGap(loop.TaskName,
                $"{flow.TaskName}: ForEach Loop's own Data Flow Task body writes to a Flat File Destination -- only a SQL destination is supported inside a loop body yet"));
            return null;
        }

        if (!DestinationInfo.IsFastLoadConfigured(flow.DestinationComponent))
        {
            gaps.Add(new GenerationGap(loop.TaskName,
                $"{flow.TaskName}: destination '{flow.DestinationComponent.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
            return null;
        }

        var entityName = TryResolveEntityName(flow.TaskName, flow.DestinationComponent, gaps);
        if (entityName is null) return null;

        // PlanForEachDataFlowLoop already required a Flat File Source with a resolvable
        // connection manager before a ForEachFileDataFlowPlan was ever built.
        var csvCmName = flow.FlatFileSource!.FlatFileSource!.ConnectionName!;
        var csvConnectionManager = FindConnectionManager(package, csvCmName)!;

        var translated = ForEachLoopEmitter.TranslateSqlTemplate(loop.FilePathExpression, loop.VariableName, "currentFile");
        if (translated is NotTranslatable notTranslatable)
        {
            gaps.Add(new GenerationGap(loop.TaskName, $"{csvCmName}'s own ConnectionString expression: {notTranslatable.Reason}"));
            return null;
        }

        var nameMode = ResolveForEachFileNameMode(loop.NameRetrievalTypeRaw, loop.TaskName, gaps);
        if (nameMode is null) return null;

        var nullableColumnNames = ResolveNullableColumnNames(flow);

        var rowTypeName = entityName + "CsvRow";
        Merge(files, gaps, CsvRowEmitter.Emit($"{ns}.Csv", rowTypeName, csvConnectionManager));
        Merge(files, gaps, ClassMapEmitter.Emit($"{ns}.Csv", rowTypeName, csvConnectionManager));

        Merge(files, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, flow.DestinationComponent, nullableColumnNames));

        var transformClassName = entityName + "Transform";
        var transformResult = TransformEmitter.Emit(new TransformRequest(
            EmitSeams: emitSeams,
            MappingNamespace: $"{ns}.Mapping",
            TransformClassName: transformClassName,
            RowTypeNamespace: $"{ns}.Csv",
            RowTypeName: rowTypeName,
            EntityNamespace: $"{ns}.Model",
            EntityName: entityName,
            SsisFnNamespace: $"{ns}.Ssis",
            Pipeline: flow.Pipeline,
            DerivedColumns: flow.DerivedColumn is null ? [] : [flow.DerivedColumn],
            DataConversions: flow.DataConversion is null ? [] : [flow.DataConversion],
            DestinationComponent: flow.DestinationComponent,
            NullableColumnNames: nullableColumnNames));
        Merge(files, gaps, transformResult.Result);
        foreach (var fn in transformResult.SsisFunctionsUsed) functionsUsed.Add(fn);

        if (transformResult.Result.Files.Count == 0) return null; // TransformEmitter already recorded why

        primaryKeysByDestination.TryGetValue(flow.DestinationComponent.RefId, out var primaryKey);
        tables.Add(new DbContextEmitter.TableSpec(entityName, flow.DestinationComponent, primaryKey));

        if (authMode is null && ResolveDatabaseAuth(package, flow.DestinationComponent) is { } auth)
            (authMode, userId, targetServer, targetDatabase) = auth;

        return new ProgramForEachDataFlowLoopStep(
            loop.TaskName, loop.Folder, loop.FileSpec, loop.Recurse, nameMode,
            "currentFile", ((TranslatedOk)translated).CSharpExpression,
            flow.FlatFileSource.Name, rowTypeName, entityName, transformClassName);
    }
}
