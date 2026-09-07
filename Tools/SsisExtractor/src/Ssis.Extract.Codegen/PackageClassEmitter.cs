namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits <c>{PackageName}.cs</c> -- the ported package itself, one generated method per SSIS
/// task/pipeline component, container nesting preserved. This is the emitter rewrite
/// (<c>Docs/Emitter-Rewrite-Plan.md</c>): the previous design (still in source control history)
/// built one giant top-level-statement <c>Program.cs</c> where every component was constructed
/// inside a <c>builder.Services.AddScoped&lt;IRowSource&lt;T&gt;&gt;(sp =&gt; {...})</c>
/// DI-registration lambda far from where it actually ran, a Sequence Container like
/// <c>SEQ_Prepare</c> left no trace in the code at all (its children were silently hoisted into a
/// package-global pre-load list), and three components run concurrently under real SSIS were
/// three indistinguishable <c>results.Add(await ...)</c> lines. None of that made the generated
/// code reviewable against a human-authored walkthrough, which is the whole point of this tool.
///
/// Every SSIS task and pipeline component here becomes its own <c>internal</c> method
/// (<c>SQL_TruncateTargets</c>, <c>DFT_ExcelImport</c>, ...), a Sequence Container becomes a
/// method that calls its own children in order (<c>SEQ_Prepare</c>), and every source/sink is
/// constructed DIRECTLY inside the method that uses it -- no DI factory lambda, no
/// <c>sp.GetRequiredService&lt;IRowSource&lt;T&gt;&gt;()</c> indirection between where a thing is
/// registered and where it runs.
///
/// <b>This phase (phase 3 of the plan) is deliberately SEQUENTIAL ONLY</b> -- every step still
/// runs one after another inside the ONE shared <c>IUnitOfWork</c>/transaction
/// <see cref="ProgramEmitter"/>'s bootstrap resolves, exactly like the design this replaces. Real
/// concurrency (a per-branch transaction, <c>Task.WhenAll</c> for genuinely parallel SSIS
/// branches) is a later phase -- see that section of the plan for why per-branch transactions are
/// a real, deliberate atomicity trade-off and not something to add casually here.
/// </summary>
/// <summary>One generated <c>internal</c> method PackageClassEmitter emitted for a single SSIS
/// task/pipeline component -- the exact (name, signature) pair actually written, not a
/// re-derivation of it. <see cref="PackageReadmeEmitter"/> is the reason this exists: rebuilding
/// the same name-collision-avoiding (<c>Reserve</c>) pass a second time to describe the README's
/// own "generated method" column would risk the two silently drifting apart the moment two
/// components share a default SSIS name (e.g. two untouched "OLE DB Source" components across
/// different Data Flow Tasks) -- returning what was actually decided, once, is the only way the
/// README can promise byte-for-byte correspondence with <c>{Package}.cs</c>.</summary>
/// <param name="RowTypeName">Only ever set for <c>Kind == "Source"</c> -- the exact row type the
/// method returns an <c>IRowSource&lt;T&gt;</c> of. <see cref="PackageGenerator"/>'s own Tier-A
/// "Source -- CSV" starter test matches back to its own <see cref="CsvSampleCandidate"/> through
/// THIS, never <see cref="SsisName"/> (a Flat File Source's own SSIS object name is free text and
/// routinely left at the schema default across every Data Flow Task in a package, so two different
/// flows' sources can and do share one -- confirmed the hard way, by a real cross-flow starter test
/// that compiled a WRONG row type into a RIGHT-looking method call before this field existed). A
/// row type name is comparably far more likely to be unique, since it's derived from the flow's own
/// destination entity name.</param>
public sealed record ComponentMethodEntry(string Kind, string SsisName, string MethodName, string Signature, string? RowTypeName = null);

/// <summary>The three method-signature templates <see cref="PackageClassEmitter"/> emits,
/// extracted so <see cref="PackageReadmeEmitter"/> renders the identical text in its own
/// "Generated method" column rather than hand-typing the same three shapes a second time.</summary>
public static class MethodSignatures
{
    public static string ForStepOrContainer(string methodName) =>
        $"internal async Task {methodName}(IUnitOfWork uow, CancellationToken ct)";

    public static string ForSource(string methodName, string rowTypeName, bool needsUow) =>
        $"internal IRowSource<{rowTypeName}> {methodName}({(needsUow ? "IUnitOfWork uow" : "")})";

    public static string ForSink(string methodName, string entityName) =>
        $"internal IBulkSink<{entityName}> {methodName}()";
}

public static class PackageClassEmitter
{
    public static string ClassName(string packageName) => $"{packageName}Package";

    public static EmitResult Emit(ProgramRequest request, out IReadOnlyList<ComponentMethodEntry> methodInventory) =>
        Emit(request, out methodInventory, out _, out _);

    /// <param name="supportsFakeHappyPath">Whether a fakes-only, no-real-connectivity RunAsync
    /// happy-path test (Docs/Generated-Tests-Plan.md's "RunAsync -- happy path" row) is safe to
    /// generate for this package. False the instant ANY source or step could reach out to
    /// something FakeUnitOfWork/PackageHarness cannot fake: a direct SQL/ADO NET source (opens a
    /// real connection to resolve its own metadata), an Excel source (reads a real .xlsx -- no
    /// Tier-A sample data exists for this shape yet), a Lookup preload (LookupCache.LoadAsync
    /// takes a raw connection STRING, never IUnitOfWork, so it is never faked regardless of what
    /// the rest of the flow does), a secondary-connection SQL step (same reason), or a Script
    /// Task (an arbitrary, hand-ported fill -- e.g. the real Package.dtsx's own
    /// SCR_NotifyAndLogProgress fill genuinely calls ctx.Uow.Context.Set&lt;T&gt;().CountAsync(),
    /// which WOULD try to open FakeUnitOfWork's fake connection string for real), or a File System
    /// Task (FileSystemActionRunner.RunAsync always calls the real System.IO.File.Copy/Move/...,
    /// no IUnitOfWork/interface indirection at all -- PackageHarness's own FileSourceOptions
    /// wiring for a File System Task key gives it a real, resolvable path, but not a SOURCE FILE
    /// WITH CONTENT ALREADY ON DISK, which only the dedicated "File System Task" starter test
    /// itself writes before calling the step directly; see ComponentTestEmitter.EmitFileSystemTaskTest's
    /// own doc comment). A CSV/fixed-width source writing to a SQL or Flat File sink is the one
    /// shape proven safe so far.</param>
    /// <param name="usesLookupPreload">Whether this package has any Lookup at all -- exposed
    /// separately from <paramref name="supportsFakeHappyPath"/> (which folds it in among several
    /// other disqualifiers) because a Lookup-bearing package's RunAsync failure-path starter test
    /// needs a genuinely different shape: its own reference-table preload now runs inside RunAsync's
    /// try block (see this method's own emitted bootstrap) and always attempts a real connection
    /// PackageHarness can never make succeed, so that -- not the ThrowOnGetBindToken fault injection
    /// every other package's test relies on -- is the failure such a package's test actually
    /// exercises.</param>
    public static EmitResult Emit(
        ProgramRequest request, out IReadOnlyList<ComponentMethodEntry> methodInventory, out bool supportsFakeHappyPath,
        out bool usesLookupPreload)
    {
        var inventory = new List<ComponentMethodEntry>();
        var flows = request.Steps.OfType<ProgramFlowStep>().Select(s => s.Flow).ToList();
        var splits = request.Steps.OfType<ProgramConditionalSplitStep>().ToList();
        var multicasts = request.Steps.OfType<ProgramMulticastStep>().ToList();
        var oleDbCommands = request.Steps.OfType<ProgramOleDbCommandStep>().ToList();
        var dataFlowLoops = request.Steps.OfType<ProgramForEachDataFlowLoopStep>().ToList();
        if (flows.Count == 0 && splits.Count == 0 && multicasts.Count == 0 && oleDbCommands.Count == 0 && dataFlowLoops.Count == 0)
        {
            methodInventory = inventory;
            supportsFakeHappyPath = false;
            usesLookupPreload = false;
            return new EmitResult([], [new GenerationGap(request.PackageName, "no Data Flow Task could be planned for this package")]);
        }

        var allSources = new List<FlowSourceSpec>();
        foreach (var source in flows.Select(f => f.Source).Concat(splits.Select(s => s.Source))
                     .Concat(multicasts.Select(m => m.Source)).Concat(oleDbCommands.Select(c => c.Source)))
        {
            allSources.Add(source);
            if (source is MergeJoinFlowSource mj) { allSources.Add(mj.LeftSource); allSources.Add(mj.RightSource); }
            if (source is AggregateFlowSource agg) allSources.Add(agg.InnerSource);
            if (source is UnionFlowSource union) allSources.AddRange(union.Sides);
        }
        var usesCsv = allSources.Any(s => s is CsvFlowSource or FixedWidthFlowSource) || dataFlowLoops.Count > 0;
        var usesExcel = allSources.Any(s => s is ExcelFlowSource);
        var usesSql = allSources.Any(s => s is SqlFlowSource or MergeJoinFlowSource or AggregateFlowSource or UnionFlowSource);
        // SqlBulkSink<T>/BulkCopyOptions (both Etl.Core.Data) are now constructed DIRECTLY inside
        // whatever method needs them -- unlike the old DI-registration-lambda design, where
        // AddBulkSink<T>() (an Etl.Core.Hosting extension method) meant Program.cs never had to
        // name SqlBulkSink itself. A Conditional Split's own branches are always SQL-sunk (no
        // Sink field at all on ProgramConditionalSplitBranch -- see its own doc comment), and a
        // ForEach-Data-Flow-Loop is likewise always SQL-sunk (PlanForEachDataFlowLoop's own gate
        // requires it).
        var usesSqlSink = flows.Any(f => f.Sink is SqlFlowSink) || splits.Count > 0
            || multicasts.Any(m => m.Branches.Any(b => b.Sink is SqlFlowSink)) || dataFlowLoops.Count > 0;
        usesLookupPreload = flows.Any(f => f.Lookup is not null);
        var secondaryConnectionNames = request.Steps.OfType<ProgramSecondaryConnectionSqlStep>()
            .Select(s => s.ConnectionManagerName).Distinct().ToList();
        var usesFlatFileSink = flows.Any(f => f.Sink is FlatFileFlowSink) || multicasts.Any(m => m.Branches.Any(b => b.Sink is FlatFileFlowSink));
        var usesRowCount = flows.Any(f => f.RowCountVariableNames is { Count: > 0 });
        var usesExcelWhereFilter = allSources.Any(s => s is ExcelFlowSource { WhereFilter: not null });
        var usesStandaloneSort = flows.Any(f => f.SortKey is not null);
        var usesMapping = flows.Count > 0 || splits.Count > 0 || multicasts.Count > 0 || dataFlowLoops.Count > 0
            || request.Steps.OfType<ProgramSqlStep>().Any() || request.Steps.OfType<ProgramSecondaryConnectionSqlStep>().Any()
            || request.Steps.OfType<ProgramForEachFileLoopStep>().Any();
        var usesScriptTasks = request.Steps.Any(s => s is ProgramScriptTaskStep);
        var usesFileSystemTask = request.Steps.Any(s => s is ProgramFileSystemStep);

        // See this overload's own doc comment for exactly why each of these disqualifies a
        // no-real-connectivity happy-path test -- allSources is already flattened through Merge
        // Join/Aggregate/Union's own nested sides, so this single check covers a SqlFlowSource
        // hiding inside any of them too.
        supportsFakeHappyPath = !allSources.Any(s => s is SqlFlowSource)
            && !usesExcel && !usesLookupPreload && secondaryConnectionNames.Count == 0
            && !usesScriptTasks && !usesFileSystemTask && dataFlowLoops.Count == 0;

        var ns = request.RootNamespace;
        var className = ClassName(request.PackageName);
        string Str(string value) => ProgramEmitter.CSharpStringLiteral(value);

        // Every method AND field name on the generated class shares one identifier namespace --
        // a container named the same as an infra helper (vanishingly unlikely, SSIS object names
        // look like "SQL_Foo"/"DFT_Bar") is defended against rather than assumed away.
        var usedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "packageVariables", "_results", "_load", "Log", "Opt", "Db", "File", "Config", "Step", "RunAsync", "RunBranchAsync",
        };
        string Reserve(string desired)
        {
            var name = desired;
            var n = 2;
            while (!usedNames.Add(name)) name = $"{desired}_{n++}";
            return name;
        }

        // Lookup cache fields -- reserved up front (before any step/container claims a name) so
        // every reference to a lookup's own field, wherever it's used, can go through this map.
        var lookups = flows.Where(f => f.Lookup is not null).Select(f => f.Lookup!)
            .DistinctBy(l => l.VariableName).ToList();
        var lookupFieldNames = lookups.ToDictionary(l => l.VariableName, l => Reserve(l.VariableName));

        // One "reached" field per dual-position failure handler (Completion/Expression-only/
        // Failure-OR-expression) -- set true the instant execution reaches that step's own call
        // site (independent of whatever its guard then decides), read in the catch block to
        // avoid re-running work that already ran in-line. See FailureHandlerPlan's own doc
        // comment.
        var reachedFlagFieldNames = request.FailureHandlers
            .Where(h => h.IsDualPosition)
            .ToDictionary(h => h.TaskName, h => Reserve(h.TaskName + "Reached"));

        // ----- shared source/sink construction, recursive for Merge Join/Union/Aggregate -----

        string EmitAggregateFunctionExpression(AggregateFunctionFieldSpec fn) => fn.AggregationTypeRaw switch
        {
            1 => $"rows.Count(r => r.{fn.SourcePropertyName} != null)",
            2 => "rows.Count",
            3 => $"rows.Select(r => r.{fn.SourcePropertyName}).Where(v => v != null).Distinct().Count()",
            4 => $"rows.All(r => r.{fn.SourcePropertyName} == null) ? null : rows.Sum(r => r.{fn.SourcePropertyName})",
            5 => $"rows.Average(r => r.{fn.SourcePropertyName})",
            6 => $"rows.Min(r => r.{fn.SourcePropertyName})",
            7 => $"rows.Max(r => r.{fn.SourcePropertyName})",
            _ => throw new InvalidOperationException($"unsupported AggregationType {fn.AggregationTypeRaw} reached PackageClassEmitter -- PackagePlanner should have rejected this"),
        };

        string BuildFlatFileSinkExpr(string entityName, FlatFileFlowSink sink)
        {
            var columns = string.Join(", ", sink.Columns.Select(c =>
                $"new FlatFileColumnFormat({Str(c.PropertyName)}, {(c.FixedWidth is { } w ? w.ToString() : "null")}, {Str(c.Delimiter)})"));
            var header = sink.HeaderLine is null ? "null" : Str(sink.HeaderLine);
            return $"new FlatFileBulkSink<{entityName}>(File({Str(sink.FileSourceKey)}), {(sink.Overwrite ? "true" : "false")}, {header}, [{columns}], Log<FlatFileBulkSink<{entityName}>>())";
        }

        void EmitSinkConstruction(CodeWriter w, string entityName, FlowSinkSpec sink, string assignPrefix)
        {
            var expr = sink is FlatFileFlowSink ffs
                ? BuildFlatFileSinkExpr(entityName, ffs)
                : $"new SqlBulkSink<{entityName}>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<{entityName}>>())";
            w.Line($"{assignPrefix}{expr};");
        }

        void EmitSource(CodeWriter w, string rowTypeName, FlowSourceSpec source, string assignPrefix)
        {
            switch (source)
            {
                case CsvFlowSource csv:
                {
                    var opts = csv.SkipRows == 0
                        ? $"new CsvSourceOptions {{ FilePath = File({Str(csv.FileSourceKey)}) }}"
                        : $"new CsvSourceOptions {{ FilePath = File({Str(csv.FileSourceKey)}), SkipRows = {csv.SkipRows} }}";
                    w.Line($"{assignPrefix}new CsvRowSource<{rowTypeName}>({Str(csv.ComponentName)}, {opts}, new {rowTypeName}Map());");
                    break;
                }
                case FixedWidthFlowSource fw:
                {
                    w.Line("var columns = new FixedWidthColumnFormat[]");
                    w.Line("{");
                    w.Indent();
                    foreach (var col in fw.Columns)
                    {
                        var widthArg = col.FixedWidth is int cw ? cw.ToString() : "null";
                        w.Line($"new FixedWidthColumnFormat({Str(col.PropertyName)}, {widthArg}),");
                    }
                    w.Dedent();
                    w.Line("};");
                    var opts = $"new FixedWidthSourceOptions {{ FilePath = File({Str(fw.FileSourceKey)}), SkipRows = {fw.SkipRows} }}";
                    w.Line($"{assignPrefix}new FixedWidthRowSource<{rowTypeName}>({Str(fw.ComponentName)}, {opts}, columns, {rowTypeName}Reader.Read);");
                    break;
                }
                case SqlFlowSource sql:
                {
                    var opts = $"new SqlSourceOptions {{ ConnectionString = SqlConnectionStringFactory.Build(Db()), CommandText = {Str(sql.CommandText)} }}";
                    // uow is passed through so this source can bind its own connection to the
                    // package's active transaction (see SqlRowSource's own doc comment) --
                    // without it, a later flow reading a table an earlier flow in the same run
                    // just wrote (still uncommitted) deadlocks.
                    w.Line($"{assignPrefix}new SqlRowSource<{rowTypeName}>({Str(sql.ComponentName)}, {opts}, {rowTypeName}Reader.Read, uow);");
                    break;
                }
                case ExcelFlowSource excel:
                {
                    var opts = $"new ExcelSourceOptions {{ FilePath = File({Str(excel.FileSourceKey)}), WorksheetName = {Str(excel.WorksheetName)}, HasHeaderRow = {(excel.HasHeaderRow ? "true" : "false")} }}";
                    w.Line(excel.WhereFilter is { } filter
                        ? $"{assignPrefix}new FilteringRowSource<{rowTypeName}>({Str(excel.ComponentName)}, new ExcelRowSource<{rowTypeName}>({Str(excel.ComponentName)}, {opts}, {rowTypeName}Reader.Read), row => {filter});"
                        : $"{assignPrefix}new ExcelRowSource<{rowTypeName}>({Str(excel.ComponentName)}, {opts}, {rowTypeName}Reader.Read);");
                    break;
                }
                case MergeJoinFlowSource mj:
                {
                    w.OpenBlock($"IRowSource<{mj.LeftRowTypeName}> BuildLeft()");
                    EmitSource(w, mj.LeftRowTypeName, mj.LeftSource, "return ");
                    w.CloseBlock();
                    w.Blank();
                    w.OpenBlock($"IRowSource<{mj.RightRowTypeName}> BuildRight()");
                    EmitSource(w, mj.RightRowTypeName, mj.RightSource, "return ");
                    w.CloseBlock();
                    w.Blank();
                    w.Line($"{assignPrefix}new MergeJoinRowSource<{mj.LeftRowTypeName}, {mj.RightRowTypeName}, {mj.KeyClrType}, {rowTypeName}>(");
                    w.Indent();
                    w.Line($"{Str(mj.ComponentName)}, BuildLeft(), BuildRight(),");
                    w.Line($"{mj.MapperClassName}.LeftKey, {mj.MapperClassName}.RightKey,");
                    w.Line($"Comparer<{mj.KeyClrType}>.Default,");
                    w.Line($"MergeJoinType.{mj.JoinType},");
                    w.Line($"{mj.MapperClassName}.Map);");
                    w.Dedent();
                    break;
                }
                case UnionFlowSource union:
                {
                    for (var i = 0; i < union.Sides.Count; i++)
                    {
                        w.OpenBlock($"IRowSource<{union.RowTypeName}> BuildSide{i}()");
                        EmitSource(w, union.RowTypeName, union.Sides[i], "return ");
                        w.CloseBlock();
                        w.Blank();
                    }
                    w.Line(union.IsSortedInterleave
                        ? $"{assignPrefix}new MergeInterleaveRowSource<{union.RowTypeName}, {union.KeyClrType}>({Str(union.ComponentName)}, BuildSide0(), BuildSide1(), row => row.{union.KeyPropertyName}, Comparer<{union.KeyClrType}>.Default);"
                        : $"{assignPrefix}new ConcatenatingRowSource<{union.RowTypeName}>({Str(union.ComponentName)}, [{string.Join(", ", Enumerable.Range(0, union.Sides.Count).Select(i => $"BuildSide{i}()"))}]);");
                    break;
                }
                case AggregateFlowSource agg:
                {
                    if (agg.LookupFilter is { } lookupFilter)
                    {
                        w.OpenBlock($"IRowSource<{agg.SourceRowTypeName}> BuildAggregateSource()");
                        w.OpenBlock($"IRowSource<{agg.SourceRowTypeName}> BuildRawSource()");
                        EmitSource(w, agg.SourceRowTypeName, agg.InnerSource, "return ");
                        w.CloseBlock();
                        w.Blank();
                        w.Line($"return new FilteringRowSource<{agg.SourceRowTypeName}>({Str(agg.ComponentName)}, BuildRawSource(), row => {lookupFieldNames[lookupFilter.LookupVariableName]}.ContainsKey(row.{lookupFilter.JoinInputPropertyName}));");
                        w.CloseBlock();
                    }
                    else
                    {
                        w.OpenBlock($"IRowSource<{agg.SourceRowTypeName}> BuildAggregateSource()");
                        EmitSource(w, agg.SourceRowTypeName, agg.InnerSource, "return ");
                        w.CloseBlock();
                    }
                    w.Blank();
                    w.Line($"{assignPrefix}new AggregateRowSource<{agg.SourceRowTypeName}, {agg.GroupByKeyClrType}, {rowTypeName}>(");
                    w.Indent();
                    w.Line($"{Str(agg.ComponentName)},");
                    w.Line("BuildAggregateSource(),");
                    w.Line(agg.LookupKey is { } lk
                        ? $"row => {lookupFieldNames[lk.LookupVariableName]}[row.{lk.JoinInputPropertyName}].{lk.ReferenceColumnName},"
                        : $"row => row.{agg.GroupByPropertyName},");
                    w.Line($"(key, rows) => new {rowTypeName}");
                    w.Line("{");
                    w.Indent();
                    w.Line($"{agg.GroupByPropertyName} = key,");
                    for (var i = 0; i < agg.Functions.Count; i++)
                    {
                        var fn = agg.Functions[i];
                        var comma = i < agg.Functions.Count - 1 ? "," : "";
                        w.Line($"{fn.OutputPropertyName} = {EmitAggregateFunctionExpression(fn)}{comma}");
                    }
                    w.Dedent();
                    w.Line("});");
                    w.Dedent();
                    break;
                }
            }
        }

        // Phase 6: a path resolved from a connection manager (the evidenced shape) is read from
        // appsettings.json at runtime via File(key) instead of an embedded, client-machine-
        // specific absolute literal; the object-model-confirmed-but-never-evidenced literal form
        // (no connection manager behind it at all) still falls back to the literal text.
        string FileSystemPathExpr(string path, string? connectionName) =>
            connectionName is null ? Str(path) : $"File({Str(connectionName)})";

        string FileSystemActionExpr(FileSystemActionPlan action) =>
            $"new FileSystemPreLoadAction(FileSystemOperation.{action.Operation}, {FileSystemPathExpr(action.SourcePath, action.SourceConnectionName)}, {(action.DestinationPath is null ? "null" : FileSystemPathExpr(action.DestinationPath, action.DestinationConnectionName))}, {(action.Overwrite ? "true" : "false")})";

        string StepDisplayName(ProgramStep step) => step switch
        {
            ProgramFlowStep f => f.Flow.StepName,
            ProgramSqlStep s => s.StepName,
            ProgramSecondaryConnectionSqlStep s => s.StepName,
            ProgramScriptTaskStep s => s.StepName,
            ProgramFileSystemStep s => s.StepName,
            ProgramForEachFileLoopStep s => s.StepName,
            ProgramForEachDataFlowLoopStep s => s.StepName,
            ProgramConditionalSplitStep s => s.StepName,
            ProgramMulticastStep s => s.StepName,
            ProgramOleDbCommandStep s => s.StepName,
            _ => throw new InvalidOperationException($"Unhandled ProgramStep type {step.GetType().Name}"),
        };

        // ----- the container tree -----

        var methods = new CodeWriter();
        methods.Indent(); // one level in, matching "inside the class body"

        // Phase 4: every pipeline component -- not just every task -- gets its own INTERNAL
        // method, so it's independently constructible (and later, testable) in isolation. Written
        // into a SEPARATE writer from `methods` because a component method is discovered and
        // emitted WHILE its owning task method is still open (e.g. while writing DFT_ExcelImport's
        // own body) -- interleaving into the same writer would corrupt DFT_ExcelImport's own
        // indent/brace nesting. Spliced in after every task method once the whole tree is walked.
        var componentMethods = new CodeWriter();
        componentMethods.Indent();

        // Only a SqlFlowSource (or a composite that nests one -- Merge Join/Union/Aggregate can
        // all have a SQL side) needs `uow`, to bind its own connection to the package's active
        // transaction (see SqlRowSource's own doc comment). Csv/FixedWidth/Excel read from a file
        // and never touch it -- declaring an unused `uow` parameter on every source method
        // regardless would be a real, visible wart in generated code meant to be read and reviewed.
        bool SourceNeedsUow(FlowSourceSpec source) => source switch
        {
            SqlFlowSource => true,
            MergeJoinFlowSource mj => SourceNeedsUow(mj.LeftSource) || SourceNeedsUow(mj.RightSource),
            UnionFlowSource union => union.Sides.Any(SourceNeedsUow),
            AggregateFlowSource agg => SourceNeedsUow(agg.InnerSource),
            _ => false,
        };

        string EmitSourceMethod(string rowTypeName, FlowSourceSpec source)
        {
            // A component's own SSIS object name is free text ("OLE DB Source" -- the exact
            // schema default nobody renames, per PackageGenerator.SanitizeIdentifier's own doc
            // comment) and not necessarily a valid C# identifier.
            var name = Reserve(PackageGenerator.SanitizeIdentifier(source.ComponentName));
            var needsUow = SourceNeedsUow(source);
            var signature = MethodSignatures.ForSource(name, rowTypeName, needsUow);
            componentMethods.OpenBlock(signature);
            EmitSource(componentMethods, rowTypeName, source, "return ");
            componentMethods.CloseBlock();
            componentMethods.Blank();
            inventory.Add(new ComponentMethodEntry("Source", source.ComponentName, name, signature, rowTypeName));
            return name;
        }

        // Mirrors SourceNeedsUow's own decision at the CALL site -- a source method that declared
        // no `uow` parameter must be called with no argument either, or it won't compile.
        string SourceCallArgs(FlowSourceSpec source) => SourceNeedsUow(source) ? "uow" : "";

        // A destination component carries no name of its own anywhere in the plan (unlike a
        // source, an OLE DB/Flat File Destination's own .dtsx object name was never threaded this
        // far) -- named after the entity it lands instead, which is just as deterministic and
        // still gives every destination its own independently-constructible method.
        string EmitSinkMethod(string entityName, FlowSinkSpec sink)
        {
            var name = Reserve($"{entityName}Destination");
            var signature = MethodSignatures.ForSink(name, entityName);
            componentMethods.OpenBlock(signature);
            EmitSinkConstruction(componentMethods, entityName, sink, "return ");
            componentMethods.CloseBlock();
            componentMethods.Blank();
            // No SSIS name is available here -- a destination component's own object name is
            // never threaded this far into the plan (see this method's own doc comment above);
            // the entity name is the closest identifying fact PackageReadmeEmitter has to show.
            inventory.Add(new ComponentMethodEntry("Sink", entityName, name, signature));
            return name;
        }

        string EmitStepMethod(ProgramStep step)
        {
            var methodName = Reserve(StepDisplayName(step));
            var signature = MethodSignatures.ForStepOrContainer(methodName);
            methods.OpenBlock(signature);

            switch (step)
            {
                case ProgramFlowStep { Flow: var flow }:
                {
                    var sourceMethodName = EmitSourceMethod(flow.RowTypeName, flow.Source);
                    methods.Line($"var source = {sourceMethodName}({SourceCallArgs(flow.Source)});");
                    if (flow.SortKey is { } sortKey)
                        methods.Line($"source = new SortingRowSource<{flow.RowTypeName}, {sortKey.KeyClrType}>({Str(flow.StepName + ".Sort")}, source, row => row.{sortKey.KeyColumnName}, Comparer<{sortKey.KeyClrType}>.Default);");
                    foreach (var variableName in flow.RowCountVariableNames ?? [])
                        methods.Line($"source = new CountingRowSource<{flow.RowTypeName}>({Str(flow.StepName + ".RowCount")}, source, packageVariables, {Str(variableName)});");
                    var transformExpr = flow.Lookup is { } flowLookup && flow.TransformNeedsLookupCache
                        ? $"new {flow.TransformClassName}({lookupFieldNames[flowLookup.VariableName]})"
                        : $"new {flow.TransformClassName}()";
                    methods.Line($"var transform = {transformExpr};");
                    var sinkMethodName = EmitSinkMethod(flow.EntityName, flow.Sink);
                    methods.Line($"var sink = {sinkMethodName}();");
                    methods.Blank();
                    methods.Line($"var step = new DataFlowStep<{flow.RowTypeName}, {flow.EntityName}>({Str(flow.StepName)}, source, transform, sink, Log<DataFlowStep<{flow.RowTypeName}, {flow.EntityName}>>());");
                    methods.Line("await Step(step, uow, ct);");
                    break;
                }
                case ProgramSqlStep sqlStep:
                    methods.Line($"var step = new ExecuteSqlStep({Str(sqlStep.StepName)}, {sqlStep.StatementClassName}.BuildStatement(), Log<ExecuteSqlStep>());");
                    methods.Line("await Step(step, uow, ct);");
                    break;
                case ProgramSecondaryConnectionSqlStep secStep:
                    methods.Line($"var connectionString = SqlConnectionStringFactory.Build(Config().GetSection({Str($"SecondaryConnections:{secStep.ConnectionManagerName}")}).Get<DatabaseOptions>()");
                    methods.Line($"    ?? throw new InvalidOperationException({Str($"The SecondaryConnections:{secStep.ConnectionManagerName} configuration section is missing.")}));");
                    methods.Line($"var step = new SecondaryConnectionSqlStep({Str(secStep.StepName)}, {Str(secStep.ConnectionManagerName)}, connectionString, {secStep.StatementClassName}.BuildStatement(), Log<SecondaryConnectionSqlStep>());");
                    methods.Line("await Step(step, uow, ct);");
                    break;
                case ProgramScriptTaskStep scriptStep:
                    methods.Line($"var step = new {scriptStep.ClassName}(packageVariables, services);");
                    methods.Line("await Step(step, uow, ct);");
                    break;
                case ProgramFileSystemStep fsStep:
                    methods.Line($"var step = new FileSystemStep({Str(fsStep.StepName)}, {FileSystemActionExpr(fsStep.Action)}, Log<FileSystemStep>());");
                    methods.Line("await Step(step, uow, ct);");
                    break;
                case ProgramForEachFileLoopStep loopStep:
                    methods.Line("var step = new ForEachLoopStep(");
                    methods.Indent();
                    methods.Line($"{Str(loopStep.StepName)},");
                    methods.Line($"new ForEachFileLoopAction(File({Str(loopStep.FileSourceKey)}), {Str(loopStep.FileSpec)}, {(loopStep.Recurse ? "true" : "false")}, ForEachFileNameMode.{loopStep.NameMode}),");
                    methods.Line($"{loopStep.CurrentFileParamName} => {loopStep.StatementClassName}.BuildStatement({loopStep.CurrentFileParamName}),");
                    methods.Line("Log<ForEachLoopStep>());");
                    methods.Dedent();
                    methods.Line("await Step(step, uow, ct);");
                    break;
                case ProgramForEachDataFlowLoopStep dfStep:
                    methods.Line($"var step = new ForEachFileDataFlowStep<{dfStep.RowTypeName}, {dfStep.EntityName}>(");
                    methods.Indent();
                    methods.Line($"{Str(dfStep.StepName)},");
                    methods.Line($"new ForEachFileLoopAction(File({Str(dfStep.FileSourceKey)}), {Str(dfStep.FileSpec)}, {(dfStep.Recurse ? "true" : "false")}, ForEachFileNameMode.{dfStep.NameMode}),");
                    methods.Line($"{dfStep.CurrentFileParamName} => new CsvRowSource<{dfStep.RowTypeName}>({Str(dfStep.SourceComponentName)}, new CsvSourceOptions {{ FilePath = {dfStep.FilePathExpression} }}, new {dfStep.RowTypeName}Map()),");
                    methods.Line($"new {dfStep.TransformClassName}(),");
                    // A ForEach-Data-Flow-Loop is always SQL-sunk -- PlanForEachDataFlowLoop's own
                    // gate requires it, matching the old emitter's usesSql-detection comment.
                    methods.Line($"new SqlBulkSink<{dfStep.EntityName}>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<{dfStep.EntityName}>>()),");
                    methods.Line($"Log<DataFlowStep<{dfStep.RowTypeName}, {dfStep.EntityName}>>());");
                    methods.Dedent();
                    methods.Line("await Step(step, uow, ct);");
                    break;
                case ProgramConditionalSplitStep splitStep:
                {
                    var splitSourceMethodName = EmitSourceMethod(splitStep.RowTypeName, splitStep.Source);
                    methods.Line($"var source = {splitSourceMethodName}({SourceCallArgs(splitStep.Source)});");
                    methods.Line($"var step = new ConditionalSplitStep<{splitStep.RowTypeName}>(");
                    methods.Indent();
                    methods.Line($"{Str(splitStep.StepName)},");
                    methods.Line("source,");
                    methods.Line($"new {splitStep.RouterClassName}(),");
                    methods.Line("[");
                    methods.Indent();
                    for (var i = 0; i < splitStep.Branches.Count; i++)
                    {
                        var branch = splitStep.Branches[i];
                        var comma = i < splitStep.Branches.Count - 1 ? "," : "";
                        // Constructed directly, not resolved by type -- two or more branches CAN
                        // share EntityName (a Union All remerge), in which case they'd all close
                        // over the same generic transform registration if this were DI-resolved;
                        // a generated transform class never has constructor dependencies, so
                        // `new` is always safe and simpler.
                        methods.Line($"new ConditionalSplitBranch<{splitStep.RowTypeName}, {branch.EntityName}>(");
                        methods.Indent();
                        methods.Line($"{Str(branch.OutputName)},");
                        methods.Line($"new {branch.TransformClassName}(),");
                        methods.Line($"new SqlBulkSink<{branch.EntityName}>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<{branch.EntityName}>>())){comma}");
                        methods.Dedent();
                    }
                    methods.Dedent();
                    methods.Line("],");
                    methods.Line($"Log<ConditionalSplitStep<{splitStep.RowTypeName}>>());");
                    methods.Dedent();
                    methods.Line("await Step(step, uow, ct);");
                    break;
                }
                case ProgramMulticastStep multicastStep:
                {
                    var multicastSourceMethodName = EmitSourceMethod(multicastStep.RowTypeName, multicastStep.Source);
                    methods.Line($"var source = {multicastSourceMethodName}({SourceCallArgs(multicastStep.Source)});");
                    methods.Line($"var step = new MulticastStep<{multicastStep.RowTypeName}>(");
                    methods.Indent();
                    methods.Line($"{Str(multicastStep.StepName)},");
                    methods.Line("source,");
                    methods.Line("[");
                    methods.Indent();
                    for (var i = 0; i < multicastStep.Branches.Count; i++)
                    {
                        var branch = multicastStep.Branches[i];
                        var comma = i < multicastStep.Branches.Count - 1 ? "," : "";
                        var sinkExpr = branch.Sink is FlatFileFlowSink ffs
                            ? BuildFlatFileSinkExpr(branch.EntityName, ffs)
                            : $"new SqlBulkSink<{branch.EntityName}>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<{branch.EntityName}>>())";
                        methods.Line($"new ConditionalSplitBranch<{multicastStep.RowTypeName}, {branch.EntityName}>(");
                        methods.Indent();
                        methods.Line($"{Str(branch.OutputName)},");
                        methods.Line($"new {branch.TransformClassName}(),");
                        methods.Line($"{sinkExpr}){comma}");
                        methods.Dedent();
                    }
                    methods.Dedent();
                    methods.Line("],");
                    methods.Line($"Log<MulticastStep<{multicastStep.RowTypeName}>>());");
                    methods.Dedent();
                    methods.Line("await Step(step, uow, ct);");
                    break;
                }
                case ProgramOleDbCommandStep cmdStep:
                    var cmdSourceMethodName = EmitSourceMethod(cmdStep.RowTypeName, cmdStep.Source);
                    methods.Line($"var source = {cmdSourceMethodName}({SourceCallArgs(cmdStep.Source)});");
                    methods.Line($"var step = new OleDbCommandStep<{cmdStep.RowTypeName}>(");
                    methods.Indent();
                    methods.Line($"{Str(cmdStep.StepName)},");
                    methods.Line("source,");
                    methods.Line($"{Str(cmdStep.SqlTemplate)},");
                    methods.Line($"row => new object?[] {{ {string.Join(", ", cmdStep.ParameterColumnNames.Select(c => $"row.{c}"))} }},");
                    methods.Line($"Log<OleDbCommandStep<{cmdStep.RowTypeName}>>());");
                    methods.Dedent();
                    methods.Line("await Step(step, uow, ct);");
                    break;
            }

            methods.CloseBlock();
            methods.Blank();
            inventory.Add(new ComponentMethodEntry("Step", StepDisplayName(step), methodName, signature));
            return methodName;
        }

        void EmitGuardedCall(CodeWriter w, ProgramStep step, string methodName, string uowArg = "uow")
        {
            var stepName = StepDisplayName(step);
            // "Reached" is set unconditionally, before the guard is even evaluated -- everything
            // before this call site having succeeded is what the failure-path copy in the catch
            // block needs to know, independent of whatever the guard itself then decides.
            if (reachedFlagFieldNames.TryGetValue(stepName, out var reachedFlag))
                w.Line($"{reachedFlag} = true;");

            if (step.Guard is { } guard)
            {
                w.Line($"// Guard: {guard.SsisExpression}");
                w.OpenBlock($"if ({guard.CSharpPredicate})");
                w.Line($"await {methodName}({uowArg}, ct);");
                w.CloseBlock();
                w.OpenBlock("else");
                w.Line($"Log<{className}>().LogInformation(\"{{Step}}: skipped -- its precedence constraint's condition ({{Condition}}) evaluated false\", {Str(stepName)}, {Str(guard.SsisExpression)});");
                w.Line($"_results.Add(new StepResult({Str(stepName)}, 0, 0, TimeSpan.Zero) {{ Skipped = true }});");
                w.CloseBlock();
            }
            else
            {
                w.Line($"await {methodName}({uowArg}, ct);");
            }
        }

        string EmitNode(Node node)
        {
            switch (node)
            {
                case StepNode sn:
                    return EmitStepMethod(sn.Step);
                case ContainerNode cn:
                {
                    var body = new CodeWriter();
                    // `body`'s own lines get spliced into `methods` via RawLine, which bypasses
                    // `methods`'s CURRENT indent rather than adding to it -- so `body` has to bake
                    // in its own absolute indent up front. `methods` sits at level 1 ("inside the
                    // class body") right up until its own OpenBlock call below pushes it to level 2
                    // for this container method's body -- so `body` needs level 2 baked in too, the
                    // same two-Indent() shape `run` uses for its own method-body content.
                    body.Indent();
                    body.Indent();
                    foreach (var child in cn.Children)
                    {
                        var childName = EmitNode(child);
                        if (child is StepNode csn) EmitGuardedCall(body, csn.Step, childName);
                        else body.Line($"await {childName}(uow, ct);");
                    }
                    var name = Reserve(cn.Name);
                    var signature = MethodSignatures.ForStepOrContainer(name);
                    methods.OpenBlock(signature);
                    foreach (var line in body.Lines) methods.RawLine(line);
                    methods.CloseBlock();
                    methods.Blank();
                    inventory.Add(new ComponentMethodEntry("Container", cn.Name, name, signature));
                    return name;
                }
                default:
                    throw new InvalidOperationException("unreachable Node type");
            }
        }

        int FirstFlowGroup(Node node) => node switch
        {
            StepNode sn => sn.Step.FlowGroup,
            ContainerNode cn => FirstFlowGroup(cn.Children[0]),
            _ => 0,
        };
        // A Sequence Container's own children all inherit ITS wave wholesale (see
        // PackageStep.Wave's own doc comment), so every descendant agrees on the value -- reading
        // just the first one is exact, not an approximation, the same reasoning FirstFlowGroup
        // already relies on.
        int FirstWave(Node node) => node switch
        {
            StepNode sn => sn.Step.Wave,
            ContainerNode cn => FirstWave(cn.Children[0]),
            _ => 0,
        };
        string NodeDisplayName(Node node) => node switch
        {
            StepNode sn => StepDisplayName(sn.Step),
            ContainerNode cn => cn.Name,
            _ => "",
        };

        var rootNodes = BuildTree(request.Steps, 0);

        // Emitter rewrite phase 7 (Docs/Emitter-Rewrite-Plan.md §4): group the package's own
        // root-level executables into contiguous runs sharing one PackageStep.Wave value -- two
        // siblings in the SAME run have no precedence constraint between them and ran CONCURRENTLY
        // under real SSIS. A run of width 1 is executed exactly as before (sequentially, inside the
        // one ambient transaction); a run of width > 1 becomes a genuine Task.WhenAll, each branch
        // in its OWN transaction (RunBranchAsync below) -- the ambient transaction is committed
        // immediately before such a wave and a fresh one begun immediately after, so no branch's own
        // independent connection can be blocked by a schema-modification lock the ambient
        // transaction still holds (the same hazard sp_bindsession exists to work around for a single
        // shared transaction; two genuinely separate transactions have no such bridge available at
        // all, so the ambient one simply must not be open while a wave runs).
        var waveGroups = new List<List<Node>>();
        foreach (var node in rootNodes)
        {
            if (waveGroups.Count == 0 || FirstWave(waveGroups[^1][0]) != FirstWave(node))
                waveGroups.Add([node]);
            else
                waveGroups[^1].Add(node);
        }
        var hasConcurrency = waveGroups.Any(g => g.Count > 1);
        // Every constructor call that hands _results to a PackageResult needs a stable snapshot
        // when it's a ConcurrentBag (no indexer, and enumeration order is unspecified) -- a plain
        // List<StepResult> already satisfies IReadOnlyList<StepResult> directly, so this is a
        // no-op cast there, keeping every already-verified non-concurrent package's generated text
        // byte-for-byte unchanged.
        var resultsExpr = hasConcurrency ? "_results.ToList()" : "_results";

        // ----- RunAsync's own body -----

        var run = new CodeWriter();
        run.Indent();
        run.Indent(); // inside the method body, one level deeper than the class body
        run.Line("await using var scope = services.CreateAsyncScope();");
        run.Line("var sp = scope.ServiceProvider;");
        run.Line("var uow = sp.GetRequiredService<IUnitOfWork>();");
        run.Line("var notifier = sp.GetRequiredService<IPackageResultNotifier>();");
        run.Line($"var logger = Log<{className}>();");
        run.Blank();

        foreach (var seed in request.VariableSeeds)
            run.Line($"packageVariables.Set({Str(seed.SsisName)}, {seed.CSharpLiteral}); // design-time default from the .dtsx");
        if (request.VariableSeeds.Count > 0) run.Blank();

        run.Line("var startedAtUtc = sp.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;");
        run.Line("startedAtUtc = new DateTime(startedAtUtc.Ticks - (startedAtUtc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);");
        run.Line("_load = new LoadContext(Guid.NewGuid(), startedAtUtc, PackageName);");
        run.Line("var stopwatch = Stopwatch.StartNew();");
        run.Blank();

        run.Line("logger.LogInformation(\"Starting {Package}...\", PackageName);");
        // Declared OUTSIDE the try block (a `var` scoped to `try` is not visible from a sibling
        // `catch`), and unconditionally -- a Lookup's own reference-table preload runs first
        // inside the try below, before BeginAsync, so a real connectivity failure there must be
        // caught the same as any other failure without attempting a RollbackAsync on a
        // transaction that was never opened (RollbackAsync throws when none is active, which
        // would mask the real exception). A concurrent wave later commits and re-begins the
        // ambient transaction mid-run for the identical reason -- both cases share one flag.
        run.Line("var uowActive = false;");
        run.OpenBlock("try");
        // Every Lookup's reference table is loaded here, before the transaction even begins --
        // IRowTransform.Map is synchronous, so a full cache has to be fully materialized before
        // any row is read regardless. Inside the try (not before it) so a real connectivity
        // failure here is rolled back/notified/exits cleanly like any other failure, rather than
        // propagating out of RunAsync unhandled.
        foreach (var lookup in lookups)
            run.Line($"{lookupFieldNames[lookup.VariableName]} = await {lookup.CacheClassName}.LoadAsync<{lookup.KeyClrType}>(SqlConnectionStringFactory.Build(Db()), r => r.{lookup.ReferenceKeyColumn}, ct);");
        if (lookups.Count > 0) run.Blank();
        run.Line("await uow.BeginAsync(IsolationLevel.ReadCommitted, ct);");
        run.Line("uowActive = true;");
        run.Blank();
        run.Line("// Fetched once, here, while this transaction's connection is guaranteed idle -- a lazy fetch");
        run.Line("// from inside a step reproduced a real deadlock (SqlRowSource reading while SqlBulkCopy streams).");
        run.Line("await uow.GetBindTokenAsync(ct);");
        run.Blank();

        var rootFlowGroups = rootNodes.Select(FirstFlowGroup).Distinct().ToList();
        var showHeaders = rootFlowGroups.Count > 1;
        var displayNum = rootFlowGroups.Select((g, i) => (g, i)).ToDictionary(x => x.g, x => x.i + 1);
        var groupDisplayNames = rootNodes.GroupBy(FirstFlowGroup)
            .ToDictionary(g => g.Key, g => string.Join(" → ", g.Select(NodeDisplayName)));

        int? lastPrintedFlowGroup = null;
        var isFirstSegment = true;
        foreach (var wave in waveGroups)
        {
            if (wave.Count == 1)
            {
                var node = wave[0];
                var group = FirstFlowGroup(node);
                if (lastPrintedFlowGroup != group)
                {
                    if (!isFirstSegment) run.Blank();
                    if (showHeaders) run.Line($"// Flow {displayNum[group]}: {groupDisplayNames[group]}");
                    lastPrintedFlowGroup = group;
                }

                var callName = EmitNode(node);
                if (node is StepNode sn) EmitGuardedCall(run, sn.Step, callName);
                else run.Line($"await {callName}(uow, ct);");
            }
            else
            {
                if (!isFirstSegment) run.Blank();
                lastPrintedFlowGroup = null; // force the next singleton run's own header to reprint
                run.Line($"// Concurrent ({wave.Count} executables -- SSIS ran these with no ordering constraint between them):");
                foreach (var node in wave) run.Line($"//   - {NodeDisplayName(node)}");
                run.Line("await uow.CommitAsync(ct);");
                run.Line("uowActive = false;");
                run.Line("await Task.WhenAll(");
                run.Indent();
                for (var wi = 0; wi < wave.Count; wi++)
                {
                    var node = wave[wi];
                    var comma = wi < wave.Count - 1 ? "," : "";
                    var callName = EmitNode(node);
                    run.OpenBlock("RunBranchAsync(async branchUow =>");
                    if (node is StepNode sn) EmitGuardedCall(run, sn.Step, callName, uowArg: "branchUow");
                    else run.Line($"await {callName}(branchUow, ct);");
                    run.CloseBlock($"}}, ct){comma}");
                }
                run.Dedent();
                run.Line(");");
                run.Line("await uow.BeginAsync(IsolationLevel.ReadCommitted, ct);");
                run.Line("uowActive = true;");
                run.Line("await uow.GetBindTokenAsync(ct);");
            }
            isFirstSegment = false;
        }
        run.Blank();

        run.Line("await uow.CommitAsync(ct);");
        run.Line("logger.LogInformation(\"{Package}: succeeded in {Ms} ms\", PackageName, stopwatch.ElapsedMilliseconds);");
        run.CloseBlock();
        run.OpenBlock("catch (Exception ex)");
        // Rollback and every failure handler deliberately use CancellationToken.None, not ct -- a
        // cancelled run still needs its own failure recorded, and still needs a chance to run its
        // own failure handlers. uowActive is unconditional now (see above): a Lookup preload
        // failure or a concurrent wave's own commit-before-run both leave it false at the moment
        // an exception is caught here, and RollbackAsync throws on an inactive transaction --
        // which would mask the real failure -- so this only rolls back when there's something to
        // roll back.
        run.OpenBlock("if (uowActive)");
        run.Line("await uow.RollbackAsync(CancellationToken.None);");
        run.CloseBlock();
        run.Line("logger.LogError(ex, \"{Package} failed after {Steps} step(s); transaction rolled back\", PackageName, _results.Count);");
        run.Blank();
        if (request.FailureHandlers.Count == 0)
        {
            run.Line($"await notifier.NotifyAsync(new PackageResult(PackageName, false, {resultsExpr}, stopwatch.Elapsed, ex), CancellationToken.None);");
        }
        else
        {
            // IUnitOfWork.ExecuteSqlWithoutTransactionAsync itself throws while a transaction is
            // still active, so this strictly follows the rollback above -- a handler can never
            // silently enlist in a transaction about to be discarded. Each isolated in its own
            // try/catch so a throwing handler never replaces the original exception.
            run.Line("var handlersRun = new List<string>();");
            foreach (var handler in request.FailureHandlers)
            {
                var tryText = $"try {{ await uow.ExecuteSqlWithoutTransactionAsync({Str(handler.Sql)}, CancellationToken.None); handlersRun.Add({Str(handler.TaskName)}); }}";
                var catchText = $"catch (Exception hEx) {{ logger.LogError(hEx, \"{{Package}}: failure handler {{Handler}} itself failed; the original failure stands\", PackageName, {Str(handler.TaskName)}); }}";

                if (!handler.IsDualPosition)
                {
                    run.Line(tryText);
                    run.Line(catchText);
                    continue;
                }

                // Also runs from its own normal position on the success path -- only attempted
                // here if execution never reached that position at all, so a later, UNRELATED
                // failure after it already ran in-line does not run it a second time.
                run.OpenBlock($"if (!{reachedFlagFieldNames[handler.TaskName]})");
                if (handler.Guard is { } failureGuard)
                {
                    run.Line($"// Guard: {failureGuard.SsisExpression}");
                    run.OpenBlock($"if ({failureGuard.CSharpPredicate})");
                    run.Line(tryText);
                    run.Line(catchText);
                    run.CloseBlock();
                }
                else
                {
                    run.Line(tryText);
                    run.Line(catchText);
                }
                run.CloseBlock();
            }
            run.Blank();
            run.Line($"await notifier.NotifyAsync(new PackageResult(PackageName, false, {resultsExpr}, stopwatch.Elapsed, ex) {{ FailureHandlersRun = handlersRun }}, CancellationToken.None);");
        }
        run.Line("return ExitCode.LoadFailed;");
        run.CloseBlock();
        run.Blank();
        run.Line($"await notifier.NotifyAsync(new PackageResult(PackageName, true, {resultsExpr}, stopwatch.Elapsed, null), ct);");
        run.Line("foreach (var step in _results) logger.LogInformation(\"{Step}: {Rows:N0} rows loaded\", step.Name, step.RowsWritten);");
        run.Line("return ExitCode.Success;");

        // ----- assemble the whole file -----

        var w2 = new CodeWriter();
        if (hasConcurrency) w2.Line("using System.Collections.Concurrent;");
        w2.Line("using System.Data;");
        w2.Line("using System.Diagnostics;");
        w2.Line("using Etl.Core.Abstractions;");
        if (usesCsv) w2.Line("using Etl.Core.Csv;");
        if (usesSql || usesSqlSink || usesFlatFileSink || usesLookupPreload || usesRowCount || usesExcelWhereFilter || usesStandaloneSort) w2.Line("using Etl.Core.Data;");
        if (usesExcel) w2.Line("using Etl.Core.Excel;");
        w2.Line("using Etl.Core.Hosting;");
        w2.Line("using Etl.Core.Notifications;");
        w2.Line("using Etl.Core.Pipeline;");
        if (usesCsv) w2.Line($"using {ns}.Csv;");
        if (usesSql) w2.Line($"using {ns}.Sql;");
        if (usesExcel) w2.Line($"using {ns}.Excel;");
        if (usesMapping) w2.Line($"using {ns}.Mapping;");
        if (usesScriptTasks) w2.Line($"using {ns}.ScriptTasks;");
        w2.Line($"using {ns}.Model;");
        if (secondaryConnectionNames.Count > 0) w2.Line("using Microsoft.Extensions.Configuration;");
        w2.Line("using Microsoft.Extensions.DependencyInjection;");
        w2.Line("using Microsoft.Extensions.Logging;");
        w2.Line("using Microsoft.Extensions.Options;");
        w2.Blank();
        w2.Line($"namespace {ns};");
        w2.Blank();
        w2.Line("/// <summary>Ported from SSIS package '" + request.PackageName + "'.</summary>");
        w2.OpenBlock($"internal sealed partial class {className}(IServiceProvider services)");

        w2.Line($"private const string PackageName = {Str(request.PackageName)};");
        w2.Line("private readonly PackageVariables packageVariables = new();");
        // A ConcurrentBag only when this package actually runs a concurrent wave -- Add() is
        // thread-safe there but the type has no indexer, so every PackageResult constructor call
        // above snapshots it via resultsExpr instead of handing the bag itself to an
        // IReadOnlyList<StepResult> parameter.
        w2.Line(hasConcurrency
            ? "private readonly ConcurrentBag<StepResult> _results = [];"
            : "private readonly List<StepResult> _results = [];");
        w2.Line("private LoadContext _load = null!;");
        foreach (var lookup in lookups)
            w2.Line($"private Dictionary<{lookup.KeyClrType}, {lookup.CacheClassName}.ReferenceRow> {lookupFieldNames[lookup.VariableName]} = null!;");
        foreach (var flagName in reachedFlagFieldNames.Values)
            w2.Line($"private bool {flagName};");
        w2.Blank();

        // internal, not private -- a "Data Flow Task -- direct invocation" starter test
        // (ComponentTestEmitter.EmitConditionalSplitDataFlowTest) reconstructs a Conditional
        // Split's own per-branch SqlBulkSink<TEntity> from outside this class (its real
        // construction, inline inside DFT_X() below, is not independently callable the way a
        // single-destination flow's own {Entity}Destination() method is), and needs these two
        // exactly like DFT_X() itself does. Still invisible outside this compiled assembly (and
        // its InternalsVisibleTo'd .Tests project) -- no change to the package's own public API.
        w2.Line("internal ILogger<T> Log<T>() => services.GetRequiredService<ILogger<T>>();");
        w2.Line("internal IOptions<T> Opt<T>() where T : class => services.GetRequiredService<IOptions<T>>();");
        w2.Line("private DatabaseOptions Db() => Opt<DatabaseOptions>().Value;");
        w2.Line("private string File(string key) => Opt<FileSourceOptions>().Value[key].ResolvedPath;");
        if (secondaryConnectionNames.Count > 0)
            w2.Line("private IConfiguration Config() => services.GetRequiredService<IConfiguration>();");
        w2.Line("private async Task Step(ILoadTask task, IUnitOfWork uow, CancellationToken ct) => _results.Add(await task.RunAsync(uow, _load, ct));");
        if (hasConcurrency)
        {
            // One independent scope/connection/transaction per concurrent branch -- a SqlTransaction
            // is bound to a single connection, so genuinely parallel branches (SSIS ran them with no
            // ordering constraint between them, see PackageStep.Wave) cannot share the package's one
            // ambient IUnitOfWork the way every sequential step still does. `body` never sees `ct`
            // as a parameter -- it closes over the caller's own `ct` directly, so a step method's
            // existing "(uow, ct)" signature needs no change to be called from here.
            w2.Blank();
            w2.OpenBlock("private async Task RunBranchAsync(Func<IUnitOfWork, Task> body, CancellationToken ct)");
            w2.Line("await using var scope = services.CreateAsyncScope();");
            w2.Line("var branchUow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();");
            w2.Line("await branchUow.BeginAsync(IsolationLevel.ReadCommitted, ct);");
            w2.Line("await branchUow.GetBindTokenAsync(ct);");
            w2.OpenBlock("try");
            w2.Line("await body(branchUow);");
            w2.Line("await branchUow.CommitAsync(ct);");
            w2.CloseBlock();
            w2.OpenBlock("catch");
            w2.Line("await branchUow.RollbackAsync(CancellationToken.None);");
            w2.Line("throw;");
            w2.CloseBlock();
            w2.CloseBlock();
        }
        w2.Blank();

        w2.OpenBlock("public async Task<int> RunAsync(CancellationToken ct)");
        foreach (var line in run.Lines) w2.RawLine(line);
        w2.CloseBlock();
        w2.Blank();

        foreach (var line in methods.Lines) w2.RawLine(line);
        foreach (var line in componentMethods.Lines) w2.RawLine(line);

        w2.CloseBlock();

        methodInventory = inventory;
        return new EmitResult([new GeneratedFile($"{request.PackageName}.cs", w2.Render())], []);
    }

    // ----- the container tree, built from PackageStep.ContainerPath -----

    private abstract class Node;
    private sealed class StepNode(ProgramStep step) : Node { public ProgramStep Step { get; } = step; }
    private sealed class ContainerNode(string name, List<Node> children) : Node
    {
        public string Name { get; } = name;
        public List<Node> Children { get; } = children;
    }

    /// <summary>Groups a flat, already-topologically-ordered step list into a tree by
    /// <see cref="ProgramStep.ContainerPath"/> -- every step sharing a common path prefix is
    /// CONTIGUOUS in the list (a Sequence Container's own recursion fully completes before its
    /// siblings continue, see <c>PackagePlanner.WalkContainer</c>), so a single left-to-right
    /// grouping pass (not a hash-map grouping across the whole list) is correct and preserves
    /// order.</summary>
    private static List<Node> BuildTree(IReadOnlyList<ProgramStep> steps, int depth)
    {
        var result = new List<Node>();
        var i = 0;
        while (i < steps.Count)
        {
            var step = steps[i];
            if (step.ContainerPath.Count <= depth)
            {
                result.Add(new StepNode(step));
                i++;
                continue;
            }

            var containerName = step.ContainerPath[depth];
            var group = new List<ProgramStep>();
            var j = i;
            while (j < steps.Count && steps[j].ContainerPath.Count > depth && steps[j].ContainerPath[depth] == containerName)
            {
                group.Add(steps[j]);
                j++;
            }
            result.Add(new ContainerNode(containerName, BuildTree(group, depth + 1)));
            i = j;
        }
        return result;
    }
}
