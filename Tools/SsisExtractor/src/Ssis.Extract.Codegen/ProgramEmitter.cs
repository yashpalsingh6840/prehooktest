namespace Ssis.Extract.Codegen;

/// <summary>How one flow reads its rows -- a Flat File Source (CSV, via a FileSourceOptions
/// key) or an OLE DB Source (SQL, via a resolved connection string + SELECT text). Exactly one
/// concrete case per flow; ProgramEmitter switches on it to emit the matching IRowSource
/// registration.</summary>
public abstract record FlowSourceSpec(string ComponentName);
public sealed record CsvFlowSource(string ComponentName, string FileSourceKey) : FlowSourceSpec(ComponentName);
public sealed record SqlFlowSource(string ComponentName, string CommandText) : FlowSourceSpec(ComponentName);

/// <summary>An Excel Source, added 2026-08-28 testing this tool against a real third-party
/// portfolio (SSIS_From_Sandeep). Uses the same <see cref="FileSourceOptions"/>/FileSourceKey
/// mechanism a <see cref="CsvFlowSource"/> does for its file path (a workbook is just another
/// named folder/filename pair), plus the worksheet name and whether to skip its own header row.</summary>
public sealed record ExcelFlowSource(string ComponentName, string FileSourceKey, string WorksheetName, bool HasHeaderRow) : FlowSourceSpec(ComponentName);

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
    string KeyClrType) : FlowSourceSpec(ComponentName);

/// <summary>One Count column of an Aggregate flow -- <see cref="OutputPropertyName"/> on the
/// destination-facing row, counting non-null values of <see cref="SourcePropertyName"/> on the
/// flow's own raw SOURCE row (SSIS's own Count aggregation excludes NULL, confirmed via a real
/// dtexec run -- see <c>PackagePlanner.AggregateCountSpec</c>'s own doc comment). Built
/// speculatively 2026-08-30.</summary>
public sealed record AggregateCountFieldSpec(string OutputPropertyName, string SourcePropertyName);

/// <summary>A Microsoft.Aggregate flow (GroupBy + Count), built speculatively 2026-08-30 -- see
/// <c>Ssis.Extract.Model.Pipeline.AggregatePayload</c>'s own doc comment for why (the one real
/// evidenced instance is unreachable, blocked by its own upstream Lookup regardless). Wraps
/// <see cref="InnerSource"/> (the flow's own raw source, e.g. a <see cref="SqlFlowSource"/>) in
/// <c>Etl.Core.Data.AggregateRowSource&lt;TSourceRow,TKey,TRow&gt;</c> -- <see cref="SourceRowTypeName"/>
/// is that raw source's own row type (TSourceRow); the outer <see cref="FlowSourceSpec.ComponentName"/>
/// carries the Aggregate component's own name, used only for the wrapped IRowSource's own
/// logging identity, matching <see cref="MergeJoinFlowSource"/>'s own convention.</summary>
public sealed record AggregateFlowSource(
    string ComponentName,
    FlowSourceSpec InnerSource,
    string SourceRowTypeName,
    string GroupByPropertyName,
    string GroupByKeyClrType,
    List<AggregateCountFieldSpec> Counts) : FlowSourceSpec(ComponentName);

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
    LookupPreload? Lookup = null);

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
public abstract record ProgramStep;
public sealed record ProgramFlowStep(ProgramFlowSpec Flow) : ProgramStep;
public sealed record ProgramSqlStep(string StepName, string Sql) : ProgramStep;

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
    List<ProgramStep> Steps);

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
        }
        // A ForEach-Data-Flow-Loop step has no top-level FlowSourceSpec at all (its own source
        // is constructed fresh, inline, per iteration -- see ProgramForEachDataFlowLoopStep's
        // own doc comment), but it is always CSV-sourced this round (PlanForEachDataFlowLoop's
        // own gate requires a Flat File Source), so it still needs the same usings/namespace.
        var usesCsv = allSources.Any(s => s is CsvFlowSource) || dataFlowLoops.Count > 0;
        var usesExcel = allSources.Any(s => s is ExcelFlowSource);
        // A Merge Join's own combined row type always lands in the .Sql namespace/folder
        // (MergeJoinEmitter's own choice, independent of what its two SIDES are sourced from),
        // and MergeJoinRowSource<>/MergeJoinType both live in Etl.Core.Data/.Abstractions -- so a
        // Merge Join flow needs these usings even when BOTH its sides happen to be CSV-sourced
        // (the real evidenced case).
        var usesSql = allSources.Any(s => s is SqlFlowSource or MergeJoinFlowSource or AggregateFlowSource);
        // A Lookup preload needs DatabaseOptions/SqlConnectionStringFactory (Etl.Core.Data, which
        // usesSql already covers when the SOURCE is SQL -- but a Lookup flow can be CSV-sourced)
        // plus IConfiguration.Get<T>() for the binder extension method.
        var usesLookupPreload = flows.Any(f => f.Lookup is not null);
        var usesFlatFileSink = flows.Any(f => f.Sink is FlatFileFlowSink) || multicasts.Any(m => m.Branches.Any(b => b.Sink is FlatFileFlowSink));
        // An OLE DB Command flow is the one flow shape with no IRowTransform/TransformEmitter
        // output at all -- a package whose ONLY flow is an OLE DB Command has no Mapping/
        // folder generated, so this using must be conditional (unlike Model, which always
        // exists: DbContextEmitter emits a table-less DbContext even then). Confirmed real: an
        // OLE DB Command-only package failed CS0234 ("namespace 'Mapping' does not exist")
        // before this fix, caught by actually building the generated project.
        var usesMapping = flows.Count > 0 || splits.Count > 0 || multicasts.Count > 0 || dataFlowLoops.Count > 0;

        var lines = new List<string> { "using Etl.Core.Abstractions;" };
        if (usesCsv) lines.Add("using Etl.Core.Csv;");
        if (usesSql || usesFlatFileSink || usesLookupPreload) lines.Add("using Etl.Core.Data;");
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
        if (usesLookupPreload) lines.Add("using Microsoft.Extensions.Configuration;");
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
            if (flow.Lookup is { } flowLookup)
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
        if (request.Steps.Any(step => step is ProgramScriptTaskStep))
        {
            lines.Add("    var packageVariables = new PackageVariables();");
            lines.Add("");
        }
        foreach (var step in request.Steps)
        {
            switch (step)
            {
                case ProgramFlowStep { Flow: var flow }:
                    var flowLocal = FlowLocalName(flow.EntityName);
                    orderedLocalNames.Add(flowLocal);
                    lines.Add($"    var {flowLocal} = new DataFlowStep<{flow.RowTypeName}, {flow.EntityName}>(");
                    lines.Add($"        \"{flow.StepName}\",");
                    lines.Add($"        sp.GetRequiredService<IRowSource<{flow.RowTypeName}>>(),");
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
        }
        lines.Add("    return new EtlPackage(");
        lines.Add("        Name: PackageName,");
        lines.Add($"        PreLoadStatements: [{string.Join(", ", request.PreLoadStatements.Select(CSharpStringLiteral))}],");
        lines.Add($"        Steps: [{string.Join(", ", orderedLocalNames)}],");
        lines.Add($"        PreLoadFileActions: [{string.Join(", ", request.PreLoadFileActions.Select(FileSystemActionExpr))}]);");
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

    /// <summary>e.g. <c>new FileSystemPreLoadAction(FileSystemOperation.Copy, "C:\...", "D:\...", true)</c> -- shared by pre-load emission and the post-flow ProgramFileSystemStep case, since both need the exact same literal expression.</summary>
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
                lines.Add("        MergeJoinType.LeftOuter,");
                lines.Add($"        {mj.MapperClassName}.Map);");
                break;
            case CsvFlowSource csv:
                EmitSourceConstruction(lines, "    ", rowTypeName, csv);
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
                lines.Add($"{indent}var csvOptions = new CsvSourceOptions {{ FilePath = fileOptions[\"{csv.FileSourceKey}\"].ResolvedPath }};");
                lines.Add($"{indent}return new CsvRowSource<{rowTypeName}>(\"{csv.ComponentName}\", csvOptions, new {rowTypeName}Map());");
                break;
            case SqlFlowSource sql:
                lines.Add($"{indent}var dbOptions = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;");
                lines.Add($"{indent}var sqlOptions = new SqlSourceOptions {{ ConnectionString = SqlConnectionStringFactory.Build(dbOptions), CommandText = {CSharpStringLiteral(sql.CommandText)} }};");
                lines.Add($"{indent}return new SqlRowSource<{rowTypeName}>(\"{sql.ComponentName}\", sqlOptions, {rowTypeName}Reader.Read);");
                break;
            case ExcelFlowSource excel:
                lines.Add($"{indent}var fileOptions = sp.GetRequiredService<IOptions<FileSourceOptions>>().Value;");
                lines.Add($"{indent}var excelOptions = new ExcelSourceOptions {{ FilePath = fileOptions[\"{excel.FileSourceKey}\"].ResolvedPath, WorksheetName = {CSharpStringLiteral(excel.WorksheetName)}, HasHeaderRow = {(excel.HasHeaderRow ? "true" : "false")} }};");
                lines.Add($"{indent}return new ExcelRowSource<{rowTypeName}>(\"{excel.ComponentName}\", excelOptions, {rowTypeName}Reader.Read);");
                break;
            case AggregateFlowSource agg:
                // Same "build the inner source via a local function" shape MergeJoinFlowSource's
                // own two sides already use -- the inner IRowSource<TSourceRow> is not a real,
                // independently-useful DI service; it exists only to feed this one
                // AggregateRowSource. Built speculatively 2026-08-30.
                lines.Add($"{indent}IRowSource<{agg.SourceRowTypeName}> BuildAggregateSource()");
                lines.Add($"{indent}{{");
                EmitSourceConstruction(lines, indent + "    ", agg.SourceRowTypeName, agg.InnerSource);
                lines.Add($"{indent}}}");
                lines.Add($"{indent}");
                lines.Add($"{indent}return new AggregateRowSource<{agg.SourceRowTypeName}, {agg.GroupByKeyClrType}, {rowTypeName}>(");
                lines.Add($"{indent}    {CSharpStringLiteral(agg.ComponentName)},");
                lines.Add($"{indent}    BuildAggregateSource(),");
                lines.Add($"{indent}    row => row.{agg.GroupByPropertyName},");
                lines.Add($"{indent}    (key, rows) => new {rowTypeName}");
                lines.Add($"{indent}    {{");
                lines.Add($"{indent}        {agg.GroupByPropertyName} = key,");
                for (var i = 0; i < agg.Counts.Count; i++)
                {
                    var count = agg.Counts[i];
                    var comma = i < agg.Counts.Count - 1 ? "," : "";
                    lines.Add($"{indent}        {count.OutputPropertyName} = rows.Count(r => r.{count.SourcePropertyName} != null){comma}");
                }
                lines.Add($"{indent}    }});");
                break;
        }
    }

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
