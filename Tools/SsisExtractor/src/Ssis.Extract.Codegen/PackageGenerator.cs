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
public static class PackageGenerator
{
    public static PackageGenerateResult Generate(PackageSpec package, string? namespacePrefix)
    {
        var ns = namespacePrefix is null ? package.ObjectName : $"{namespacePrefix}.{package.ObjectName}";
        var files = new List<GeneratedFile>();
        var gaps = new List<GenerationGap>();

        var plan = PackagePlanner.Plan(package);
        gaps.AddRange(plan.Gaps);

        if (plan.Flows.Count == 0)
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
                gaps.Add(new GenerationGap(flow.TaskName,
                    $"Data Flow Task '{flow.TaskName}' contains a Microsoft.Lookup component ('{lookup.Name}') -- SSIS does not persist a Lookup's join key anywhere in the saved .dtsx, so this tool cannot safely generate a join condition without guessing. A best-effort reference-table cache scaffold was generated at Mapping/{cacheClassName}.cs, but this flow's transform/Program.cs wiring were NOT generated. To finish by hand: pick the join key, write a Map() that calls {cacheClassName}.LoadAsync at startup and looks up by that key, and add a new DataFlowStep to Program.cs following the pattern of this package's other flows."));
                continue;
            }

            if (flow.ConditionalSplit is { } split)
            {
                var splitStep = GenerateConditionalSplitFlow(
                    package, ns, flow, split, fileSourceEntries, files, tables, functionsUsed,
                    primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase);
                if (splitStep is not null) wiredSplits[flow] = splitStep;
                continue;
            }

            if (flow.MergeJoin is { } mergeJoin)
            {
                var mergeJoinFlow = GenerateMergeJoinFlow(
                    package, ns, flow, mergeJoin, fileSourceEntries, files, tables, functionsUsed,
                    primaryKeysByDestination, gaps, ref authMode, ref userId, ref targetServer, ref targetDatabase);
                if (mergeJoinFlow is not null) wiredFlows[flow] = mergeJoinFlow;
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

            // OLE DB/ADO NET destinations still require a Derived Column or Data Conversion --
            // a plain direct-copy pipeline's transform is a separate, still-unproven generator
            // path for a SQL destination (see PackagePlanner's own "direct-copy" notes). A Flat
            // File Destination is deliberately exempted: the two real evidenced instances
            // (RBC_Demo_ETL's own DFT_ExportDelimited/DFT_ExportFixedWidth) are BOTH pure
            // column-copy exports with no transform at all, and TransformEmitter already handles
            // an empty DerivedColumns list correctly (every column just becomes a plain
            // `row.ColumnName` passthrough assignment) -- there is nothing SQL-specific in that
            // code path to be unproven about.
            if (!isFlatFileDestination && flow.DerivedColumn is null && flow.DataConversion is null)
            {
                gaps.Add(new GenerationGap(flow.TaskName, "no Derived Column or Data Conversion found in this Data Flow Task -- a direct-copy pipeline's transform is not generated yet, so this flow is not wired into Program.cs"));
                continue;
            }

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
                MappingNamespace: $"{ns}.Mapping",
                TransformClassName: transformClassName,
                RowTypeNamespace: programSource is CsvFlowSource ? $"{ns}.Csv" : $"{ns}.Sql",
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

        // A package whose only successfully-wired flows are Flat File Destinations has
        // tables.Count == 0 but still needs a (now possibly-empty) DbContext class -- Program.cs
        // always emits AddEtlDbContext<T>()/IUnitOfWork's own transaction wrapping needs a real
        // DbContext type regardless of whether anything maps to a SQL table (see
        // DbContextEmitter's own updated doc comment). Still correctly skipped when NOTHING
        // wired at all (an all-gapped package), matching existing behavior.
        if (tables.Count > 0 || wiredFlows.Count > 0 || wiredSplits.Count > 0)
            Merge(files, gaps, DbContextEmitter.Emit($"{ns}.Model", $"{package.ObjectName}DbContext", tables));

        Merge(files, gaps, SsisFnEmitter.Emit($"{ns}.Ssis", functionsUsed));

        // Built by walking plan.Steps (not wiredFlows.Values) so a SqlStep between two flows
        // keeps its real position -- a flow that never made it into wiredFlows/wiredSplits
        // (failed a gate above) simply contributes nothing here, same as it always has.
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
                case SqlStep sqlStep:
                    programSteps.Add(new ProgramSqlStep(sqlStep.TaskName, sqlStep.Sql));
                    break;
                case FileSystemStep fileSystemStep:
                    programSteps.Add(new ProgramFileSystemStep(fileSystemStep.TaskName, fileSystemStep.Action));
                    break;
            }
        }

        if (wiredFlows.Count > 0 || wiredSplits.Count > 0)
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
    /// against -- the flow's own destination, or a split's default branch's destination.</summary>
    private static (string RowTypeName, FlowSourceSpec Source)? ResolveFlowSource(
        PackageSpec package, string ns, string rowTypeBaseName, DataFlowPlan flow, PipelineComponentSpec destinationForSqlCheck,
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

        gaps.Add(new GenerationGap(flow.TaskName, "no Flat File Source, OLE DB Source, or ADO NET Source (with a resolvable connection manager) found -- only [Flat File Source|OLE DB Source|ADO NET Source] -> [Derived Column] -> [OLE DB Destination|ADO NET Destination] is supported yet"));
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
        List<GenerationGap> gaps, ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase)
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
            NullableColumnNames: mergeJoinResult.NullableColumnNames));
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
        ref string? authMode, ref string? userId, ref string? targetServer, ref string? targetDatabase)
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
        var rowTypeNamespace = programSource is CsvFlowSource ? $"{ns}.Csv" : $"{ns}.Sql";

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

    /// <summary>Resolves an OLE DB or ADO NET Source into a SqlFlowSource -- dispatches to
    /// <see cref="BuildOleDbFlowSource"/> or <see cref="BuildAdoNetFlowSource"/> after the one
    /// check both share: a resolvable connection manager targeting the same server/database as
    /// the flow's own destination (a second source connection is not supported yet).</summary>
    private static SqlFlowSource? BuildSqlFlowSource(
        PackageSpec package, string taskName, PipelineComponentSpec source,
        PipelineComponentSpec destinationComponent, List<GenerationGap> gaps)
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
        // only SQL connection this flow uses.
        if (destinationComponent.ComponentClassId != "Microsoft.FlatFileDestination")
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
}
