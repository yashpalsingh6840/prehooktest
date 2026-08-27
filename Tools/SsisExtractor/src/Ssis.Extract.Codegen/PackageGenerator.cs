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

            var entityName = TryResolveEntityName(flow.TaskName, flow.DestinationComponent, gaps);
            if (entityName is null) continue;

            // Milestone 1 covers fast-load destinations only (plan §0.3's own resolution --
            // AccessMode itself is never decoded, this is the minimal check that decision
            // settled on). Anything else needs row-by-row insert generation this tool doesn't
            // have yet.
            if (flow.DestinationComponent.OleDbDestination?.FastLoadMaxInsertCommitSize is null
                && flow.DestinationComponent.OleDbDestination?.FastLoadOptions is null)
            {
                gaps.Add(new GenerationGap(flow.TaskName, $"destination '{flow.DestinationComponent.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
                continue;
            }

            var sourceResult = ResolveFlowSource(package, ns, entityName, flow, flow.DestinationComponent, fileSourceEntries, files, gaps);
            if (sourceResult is null) continue; // ResolveFlowSource already added the reason
            var (rowTypeName, programSource) = sourceResult.Value;

            var transformClassName = entityName + "Transform";
            Merge(files, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, flow.DestinationComponent));

            if (flow.DerivedColumn is null)
            {
                gaps.Add(new GenerationGap(flow.TaskName, "no Derived Column found in this Data Flow Task -- a direct-copy pipeline's transform is not generated yet, so this flow is not wired into Program.cs"));
                continue;
            }

            primaryKeysByDestination.TryGetValue(flow.DestinationComponent.RefId, out var primaryKey);
            tables.Add(new DbContextEmitter.TableSpec(entityName, flow.DestinationComponent, primaryKey));

            var transformResult = TransformEmitter.Emit(new TransformRequest(
                MappingNamespace: $"{ns}.Mapping",
                TransformClassName: transformClassName,
                RowTypeNamespace: programSource is CsvFlowSource ? $"{ns}.Csv" : $"{ns}.Sql",
                RowTypeName: rowTypeName,
                EntityNamespace: $"{ns}.Model",
                EntityName: entityName,
                SsisFnNamespace: $"{ns}.Ssis",
                Pipeline: flow.Pipeline,
                DerivedColumn: flow.DerivedColumn,
                DestinationComponent: flow.DestinationComponent));
            Merge(files, gaps, transformResult.Result);
            foreach (var fn in transformResult.SsisFunctionsUsed) functionsUsed.Add(fn);

            if (transformResult.Result.Files.Count == 0)
            {
                // TransformEmitter itself already recorded why (e.g. every column untranslatable) --
                // don't also wire a Program.cs flow around a transform class that doesn't exist.
                continue;
            }

            wiredFlows[flow] = new ProgramFlowSpec(flow.TaskName, programSource, rowTypeName, entityName, transformClassName);

            if (authMode is null && ResolveDatabaseAuth(package, flow.DestinationComponent) is { } auth)
            {
                (authMode, userId, targetServer, targetDatabase) = auth;
            }
        }

        if (tables.Count > 0)
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
            }
        }

        if (wiredFlows.Count > 0 || wiredSplits.Count > 0)
        {
            Merge(files, gaps, ProgramEmitter.Emit(new ProgramRequest(
                PackageName: package.ObjectName,
                RootNamespace: ns,
                DbContextTypeName: $"{package.ObjectName}DbContext",
                PreLoadStatements: plan.PreLoadStatements,
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
        var openRowset = destinationComponent.OleDbDestination?.OpenRowset;
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
        List<FileSourceEntryRequest> fileSourceEntries, List<GeneratedFile> files, List<GenerationGap> gaps)
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
            Merge(files, gaps, SqlRowEmitter.Emit($"{ns}.Sql", rowTypeName, oleDbSource));
            Merge(files, gaps, SqlRowReaderEmitter.Emit($"{ns}.Sql", rowTypeName, oleDbSource));

            return (rowTypeName, sqlSource);
        }

        gaps.Add(new GenerationGap(flow.TaskName, "no Flat File Source or OLE DB Source (with a resolvable connection manager) found -- only [Flat File Source|OLE DB Source] -> [Derived Column] -> OLE DB Destination is supported yet"));
        return null;
    }

    /// <summary>
    /// Generates a whole Conditional Split flow: one shared source (via <see cref="ResolveFlowSource"/>),
    /// the flow's own shared Derived Column (if any, unchanged from the single-destination path
    /// -- scope decision 1, CLAUDE.md), one router (<see cref="RouterEmitter"/>), and one
    /// Model/Mapping/DbSet trio per branch. Buffers every branch's generated files/table/
    /// functions-used locally and only commits them (plus the router's own file) to the real
    /// <paramref name="files"/>/<paramref name="tables"/>/<paramref name="functionsUsed"/> once
    /// EVERY branch and the router succeed -- scope decision 2 (no partial split generation).
    /// A flow's own source-row-type file(s) (emitted eagerly by ResolveFlowSource, before any
    /// branch is attempted) are the one exception: they describe the source, not any branch, so
    /// they're valid output regardless of whether a branch downstream ends up failing -- an
    /// orphaned-but-harmless Sql/Csv row-type file is not "half-wired" the way a partially-wired
    /// Program.cs would be.
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

        var sourceResult = ResolveFlowSource(package, ns, flowBaseName, flow, defaultDestination, fileSourceEntries, files, gaps);
        if (sourceResult is null) return null;
        var (rowTypeName, programSource) = sourceResult.Value;
        var rowTypeNamespace = programSource is CsvFlowSource ? $"{ns}.Csv" : $"{ns}.Sql";

        if (flow.DerivedColumn is null)
        {
            gaps.Add(new GenerationGap(flow.TaskName, "no Derived Column found in this Data Flow Task -- a direct-copy pipeline's transform is not generated yet, so this flow is not wired into Program.cs"));
            return null;
        }

        var routerClassName = SanitizeIdentifier(split.Component.Name) + "Router";
        var routerResult = RouterEmitter.Emit($"{ns}.Mapping", routerClassName, rowTypeNamespace, rowTypeName, split);
        if (routerResult.Files.Count == 0)
        {
            gaps.AddRange(routerResult.Gaps);
            return null;
        }

        var branchFiles = new List<GeneratedFile>();
        var branchTables = new List<DbContextEmitter.TableSpec>();
        var branchFunctionsUsed = new HashSet<string>();
        var branches = new List<ProgramConditionalSplitBranch>();
        (string AuthMode, string? UserId, string? Server, string? Database)? resolvedAuth = null;

        foreach (var branch in split.Branches)
        {
            // Same fast-load-only gate as the single-destination path, applied per branch.
            if (branch.Destination.OleDbDestination?.FastLoadMaxInsertCommitSize is null
                && branch.Destination.OleDbDestination?.FastLoadOptions is null)
            {
                gaps.Add(new GenerationGap(flow.TaskName, $"Conditional Split branch '{branch.OutputName}': destination '{branch.Destination.Name}' is not configured for fast load -- row-by-row insert generation is not supported yet"));
                return null;
            }

            var entityName = TryResolveEntityName(flow.TaskName, branch.Destination, gaps);
            if (entityName is null) return null;

            var branchTransformClassName = entityName + "Transform";
            Merge(branchFiles, gaps, EntityEmitter.Emit($"{ns}.Model", entityName, branch.Destination));

            primaryKeysByDestination.TryGetValue(branch.Destination.RefId, out var primaryKey);
            branchTables.Add(new DbContextEmitter.TableSpec(entityName, branch.Destination, primaryKey));

            var transformResult = TransformEmitter.Emit(new TransformRequest(
                MappingNamespace: $"{ns}.Mapping",
                TransformClassName: branchTransformClassName,
                RowTypeNamespace: rowTypeNamespace,
                RowTypeName: rowTypeName,
                EntityNamespace: $"{ns}.Model",
                EntityName: entityName,
                SsisFnNamespace: $"{ns}.Ssis",
                Pipeline: flow.Pipeline,
                DerivedColumn: flow.DerivedColumn,
                DestinationComponent: branch.Destination));
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
        files.AddRange(routerResult.Files);

        if (authMode is null && resolvedAuth is { } resolved)
            (authMode, userId, targetServer, targetDatabase) = resolved;

        return new ProgramConditionalSplitStep(flow.TaskName, programSource, rowTypeName, routerClassName, branches);
    }

    /// <summary>Resolves an OLE DB Source into a SqlFlowSource, building the actual SELECT
    /// text per AccessMode: 0 (OpenRowset) builds <c>SELECT [Ext] AS [Comp], ... FROM
    /// {OpenRowset}</c> from ColumnMappings, guaranteeing every column resolves later by the
    /// pipeline's own buffer column name; 2 (SqlCommand) emits the author's own SQL verbatim
    /// (this tool never rewrites recorded SQL text) plus a non-fatal gap stating the
    /// assumption that the result set's own column names/aliases already match the buffer's
    /// column names -- a fact this tool cannot verify from the spec alone. Any other
    /// AccessMode, or a source whose connection manager can't be resolved or targets a
    /// different server/database than the flow's own destination (a second source connection
    /// is not supported yet), is a fatal gap for this flow -- returns null, with the reason
    /// already added to <paramref name="gaps"/>.</summary>
    private static SqlFlowSource? BuildSqlFlowSource(
        PackageSpec package, string taskName, PipelineComponentSpec oleDbSource,
        PipelineComponentSpec destinationComponent, List<GenerationGap> gaps)
    {
        var payload = oleDbSource.OleDbSource;
        if (payload?.ConnectionName is not { } cmName || FindConnectionManager(package, cmName) is not { } sourceCm
            || sourceCm.Parsed is not { } sourceParsed)
        {
            gaps.Add(new GenerationGap(taskName, $"OLE DB Source '{oleDbSource.Name}' has no resolvable connection manager"));
            return null;
        }

        var destCmName = destinationComponent.OleDbDestination?.ConnectionName;
        var destParsed = destCmName is not null ? FindConnectionManager(package, destCmName)?.Parsed : null;
        if (destParsed is null || sourceParsed.Server != destParsed.Server || sourceParsed.Database != destParsed.Database)
        {
            gaps.Add(new GenerationGap(taskName, $"OLE DB Source '{oleDbSource.Name}' targets a different server/database than this flow's own destination -- a separate source connection is not supported yet"));
            return null;
        }

        switch (payload.AccessMode)
        {
            case 0:
                if (string.IsNullOrEmpty(payload.OpenRowset) || payload.ColumnMappings.Count == 0)
                {
                    gaps.Add(new GenerationGap(taskName, $"OLE DB Source '{oleDbSource.Name}' has AccessMode=0 (OpenRowset) but no OpenRowset/ColumnMappings to build a SELECT from"));
                    return null;
                }
                var columns = string.Join(", ", payload.ColumnMappings.Select(m => $"[{m.ExternalColumnName}] AS [{m.ComponentColumnName}]"));
                return new SqlFlowSource(oleDbSource.Name, $"SELECT {columns} FROM {payload.OpenRowset}");

            case 2:
                if (string.IsNullOrEmpty(payload.SqlCommand))
                {
                    gaps.Add(new GenerationGap(taskName, $"OLE DB Source '{oleDbSource.Name}' has AccessMode=2 (SQL command) but no SqlCommand text"));
                    return null;
                }
                gaps.Add(new GenerationGap(taskName, $"OLE DB Source '{oleDbSource.Name}' uses a SqlCommand -- generated code assumes each result-set column is named/aliased exactly like the pipeline's own buffer column name; verify before running. This tool never rewrites the author's own SQL.", IsBlocking: false));
                return new SqlFlowSource(oleDbSource.Name, payload.SqlCommand);

            default:
                gaps.Add(new GenerationGap(taskName, $"OLE DB Source '{oleDbSource.Name}' has AccessMode={(payload.AccessMode?.ToString() ?? "null")} -- only OpenRowset (0) and SQL command (2) are supported yet"));
                return null;
        }
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

    /// <summary>"SqlLogin" (ConnectionManagerSpec.Parsed.AuthMode) -> SqlServer auth (user
    /// secrets, per ProjectEmitter); anything else -> Windows auth. Resolved from the
    /// DESTINATION's own connection manager only -- source CSV connection managers have no
    /// auth mode of their own to resolve. Also carries Server/Database along for the CLI's
    /// appsettings.Shared.json -- same connection manager, same call, no reason to re-look it
    /// up separately.</summary>
    private static (string AuthMode, string? UserId, string? Server, string? Database)? ResolveDatabaseAuth(PackageSpec package, PipelineComponentSpec destinationComponent)
    {
        var cmName = destinationComponent.OleDbDestination?.ConnectionName;
        if (cmName is null || FindConnectionManager(package, cmName) is not { } cm || cm.Parsed is not { } parsed) return null;

        return parsed.AuthMode == "SqlLogin"
            ? ("SqlServer", parsed.UserId, parsed.Server, parsed.Database)
            : ("Windows", null, parsed.Server, parsed.Database);
    }
}
