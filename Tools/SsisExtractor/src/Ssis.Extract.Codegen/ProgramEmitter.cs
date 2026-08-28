namespace Ssis.Extract.Codegen;

/// <summary>How one flow reads its rows -- a Flat File Source (CSV, via a FileSourceOptions
/// key) or an OLE DB Source (SQL, via a resolved connection string + SELECT text). Exactly one
/// concrete case per flow; ProgramEmitter switches on it to emit the matching IRowSource
/// registration.</summary>
public abstract record FlowSourceSpec(string ComponentName);
public sealed record CsvFlowSource(string ComponentName, string FileSourceKey) : FlowSourceSpec(ComponentName);
public sealed record SqlFlowSource(string ComponentName, string CommandText) : FlowSourceSpec(ComponentName);

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
    FlowSinkSpec Sink);

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

/// <summary>Reuses PackagePlanner's own FileSystemActionPlan verbatim rather than a parallel
/// Program*-namespaced DTO -- unlike ProgramFlowSpec/ProgramConditionalSplitBranch, there is no
/// generator-owned NAMING decision to add on top (no entity/transform class name), just literal
/// operation/paths already fully resolved by the planner.</summary>
public sealed record ProgramFileSystemStep(string StepName, FileSystemActionPlan Action) : ProgramStep;

public sealed record ProgramConditionalSplitStep(
    string StepName,
    FlowSourceSpec Source,
    string RowTypeName,
    string RouterClassName,
    List<ProgramConditionalSplitBranch> Branches) : ProgramStep;

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
        if (flows.Count == 0 && splits.Count == 0)
            return new EmitResult([], [new GenerationGap(request.PackageName, "no Data Flow Task could be planned for this package")]);

        var allSources = new List<FlowSourceSpec>();
        foreach (var source in flows.Select(f => f.Source).Concat(splits.Select(s => s.Source)))
        {
            allSources.Add(source);
            if (source is MergeJoinFlowSource mj) { allSources.Add(mj.LeftSource); allSources.Add(mj.RightSource); }
        }
        var usesCsv = allSources.Any(s => s is CsvFlowSource);
        // A Merge Join's own combined row type always lands in the .Sql namespace/folder
        // (MergeJoinEmitter's own choice, independent of what its two SIDES are sourced from),
        // and MergeJoinRowSource<>/MergeJoinType both live in Etl.Core.Data/.Abstractions -- so a
        // Merge Join flow needs these usings even when BOTH its sides happen to be CSV-sourced
        // (the real evidenced case).
        var usesSql = allSources.Any(s => s is SqlFlowSource or MergeJoinFlowSource);
        var usesFlatFileSink = flows.Any(f => f.Sink is FlatFileFlowSink);

        var lines = new List<string> { "using Etl.Core.Abstractions;" };
        if (usesCsv) lines.Add("using Etl.Core.Csv;");
        if (usesSql || usesFlatFileSink) lines.Add("using Etl.Core.Data;");
        lines.Add("using Etl.Core.Hosting;");
        lines.Add("using Etl.Core.Notifications;");
        lines.Add("using Etl.Core.Pipeline;");
        if (usesCsv) lines.Add($"using {request.RootNamespace}.Csv;");
        if (usesSql) lines.Add($"using {request.RootNamespace}.Sql;");
        lines.Add($"using {request.RootNamespace}.Mapping;");
        lines.Add($"using {request.RootNamespace}.Model;");
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
        // AddBulkSink<T>() path unconditionally.
        var allBranchEntityNames = splits.SelectMany(s => s.Branches.Select(b => b.EntityName));
        var sqlSinkEntityNames = flows.Where(f => f.Sink is SqlFlowSink).Select(f => f.EntityName).Concat(allBranchEntityNames).Distinct();
        foreach (var entityName in sqlSinkEntityNames)
            lines.Add($"builder.Services.AddBulkSink<{entityName}>();");
        foreach (var flow in flows.Where(f => f.Sink is FlatFileFlowSink))
            EmitFlatFileSinkRegistration(lines, flow.EntityName, (FlatFileFlowSink)flow.Sink);
        lines.Add("builder.Services.AddEmailNotifications(builder.Configuration);");
        lines.Add("");

        foreach (var flow in flows)
        {
            EmitRowSourceRegistration(lines, flow.RowTypeName, flow.Source);
            lines.Add($"builder.Services.AddScoped<IRowTransform<{flow.RowTypeName}, {flow.EntityName}>, {flow.TransformClassName}>();");
            lines.Add("");
        }

        foreach (var split in splits)
        {
            EmitRowSourceRegistration(lines, split.RowTypeName, split.Source);
            lines.Add($"builder.Services.AddScoped<IRowRouter<{split.RowTypeName}>, {split.RouterClassName}>();");
            lines.Add("");
        }

        lines.Add("builder.Services.AddScoped<IEtlPackage>(sp =>");
        lines.Add("{");
        var orderedLocalNames = new List<string>();
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
                case ProgramFileSystemStep fsStep:
                    var fsLocal = SqlStepLocalName(fsStep.StepName); // same lowercase-first convention, generic enough to reuse
                    orderedLocalNames.Add(fsLocal);
                    lines.Add($"    var {fsLocal} = new FileSystemStep(\"{fsStep.StepName}\", {FileSystemActionExpr(fsStep.Action)}, sp.GetRequiredService<ILogger<FileSystemStep>>());");
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
    /// string literal is a compile error (CS1010), not just cosmetically wrong.</summary>
    private static string CSharpStringLiteral(string value)
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
