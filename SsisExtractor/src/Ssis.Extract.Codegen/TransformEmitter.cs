using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;
using Ssis.Runtime.Expressions;

using Ssis.Extract.Model.Analysis;

namespace Ssis.Extract.Codegen;

/// <summary>Everything TransformEmitter needs to name and locate one Data Flow Task's pieces.
/// A parameter object rather than a long positional parameter list -- not a shared
/// abstraction, just this one emitter's own inputs.</summary>
public sealed record TransformRequest(
    string MappingNamespace,
    string TransformClassName,
    string RowTypeNamespace,
    string RowTypeName,
    string EntityNamespace,
    string EntityName,
    string SsisFnNamespace,
    PipelineSpec Pipeline,
    IReadOnlyList<PipelineComponentSpec> DerivedColumns,
    PipelineComponentSpec DestinationComponent,
    IReadOnlySet<string>? NullableColumnNames = null,
    IReadOnlyList<PipelineComponentSpec>? DataConversions = null,
    // True only for a Merge Join flow's own call (GenerateMergeJoinFlow): MergeJoinEmitter
    // builds RowTypeName as its OWN combined row type, already resolving every output column's
    // true value itself (including through a Data Conversion, via its own SourceColumnLineageId
    // walk) -- independent of, and not threaded through, this request's own DerivedColumns/
    // DataConversions. The unrecognized-producer gate below has no way to know that, so it must
    // be skipped entirely for this one caller rather than misidentifying an already-resolved
    // Data Conversion column as unsupported (found real 2026-08-30 testing the gate against
    // RBC_Demo_ETL's own DFT_SortAndMergeJoin).
    bool TrustAllPassthroughColumns = false,
    LookupJoinSpec? LookupJoin = null,
    // --seams. When true, a column produced by a Script Component becomes a `private partial`
    // Fill_{Column} seam a human implements in a second part of this partial class, instead of
    // being silently omitted. Opt-in because it deliberately makes the generated project FAIL to
    // build (CS8795) until every seam is filled -- which is the point: today a package missing
    // six Script-Component columns still builds clean, so "it compiles" means less than it looks.
    // Scoped to Script Components only (Tier 2, source present in the .dtsx). Any OTHER
    // unrecognized producer keeps its plain gap: that is missing TOOL support, and offering a
    // per-package hand-patch there would hide one systemic emitter gap behind N patches.
    bool EmitSeams = false);

/// <summary>
/// How a full-cache Lookup's added columns are resolved, once a human has confirmed the join key
/// SSIS does not persist (see <c>GapDecisionSpec</c>). <see cref="OutputToReferenceColumn"/> maps
/// each of the Lookup's own output column names to the reference-table column it copies from --
/// read from the output column's own <c>CopyFromReferenceColumn</c> custom property, which IS
/// persisted (unlike the join key), so only the key itself ever needed deciding.
/// </summary>
public sealed record LookupJoinSpec(
    string CacheClassName,
    string CacheParameterName,
    string InputColumnName,
    string KeyClrTypeName,
    IReadOnlyDictionary<string, string> OutputToReferenceColumn);

/// <summary>
/// Emits one Data Flow Task's Derived Column transform, e.g.
/// LoadEmployees.Mapping.EmployeeTransform. A destination column is treated as COMPUTED when
/// its name matches one of <see cref="TransformRequest.DerivedColumns"/>' own output columns
/// (its Expression gets translated via ExpressionTranslator); every other destination column
/// is a plain passthrough (<c>row.ColumnName</c>) -- this mirrors CLAUDE.md's own
/// passthrough-lineage finding (a synchronous transform's untouched columns are never
/// re-emitted as its own output columns) and matches the hand-written EmployeeTransform/
/// DepartmentTransform/DesignationTransform exactly (both call sites there pass a single-
/// element list). A Conditional Split branch that passes through its own "tag" Derived Column
/// after the split (see PackagePlanner.ResolveBranch) passes that column here too, alongside
/// any Derived Column shared upstream of the split -- computed columns are merged across every
/// list entry, first-registered wins on a name collision (not evidenced in any real package;
/// each Derived Column step normally adds distinct new columns).
/// </summary>
public static class TransformEmitter
{
    public sealed record TransformEmitResult(EmitResult Result, IReadOnlySet<string> SsisFunctionsUsed);

    public static TransformEmitResult Emit(TransformRequest request)
    {
        var gaps = new List<GenerationGap>();
        var functionsUsed = new HashSet<string>();
        var lineage = LineageBuilder.Build(request.Pipeline);
        var columnTypes = BuildColumnTypeLookup(request.Pipeline);

        var computedByName = new Dictionary<string, DerivedExpression>();
        var replacedByName = new Dictionary<string, DerivedExpression>();
        foreach (var derivedColumn in request.DerivedColumns)
        {
            foreach (var column in derivedColumn.Outputs
                .Where(o => o.IsErrorOut != true)
                .SelectMany(o => o.Columns)
                .Where(c => c.Expression is not null))
            {
                computedByName.TryAdd(column.Name, new DerivedExpression(column.Name, column.RefId, column.Expression, column.FriendlyExpression));
            }

            // "Replace <column>" mode: the expression lives on the readWrite INPUT column and no
            // output column is declared, so the column keeps its upstream lineageId and used to
            // resolve straight back to the true source -- generating a plain passthrough of the
            // RAW value while real SSIS applied the expression. Silently wrong data, zero gaps.
            foreach (var input in derivedColumn.Inputs)
            {
                foreach (var column in input.Columns.Where(c => c.Expression is not null))
                {
                    replacedByName.TryAdd(column.CachedName, new DerivedExpression(column.CachedName, column.RefId, column.Expression, column.FriendlyExpression));
                }
            }
        }

        // A Data Conversion output column has no Expression (see DataConvertPayload's own doc
        // comment) -- it's keyed separately here rather than folded into computedByName, since
        // it needs a different translation path (a SsisFn.To*() call against the RAW source
        // column, resolved via the "DataConversion" lineage edge LineageBuilder emits, not
        // ExpressionTranslator).
        var convertedByName = new Dictionary<string, DataConversionColumnSpec>();
        foreach (var dataConversion in request.DataConversions ?? [])
        {
            foreach (var column in dataConversion.DataConvert?.Columns ?? [])
                convertedByName.TryAdd(column.OutputColumnName, column);
        }

        var resolved = PipelineResolver.ResolveDestinationInput(request.DestinationComponent);
        foreach (var unresolved in resolved.Unresolved)
            gaps.Add(new GenerationGap($"{request.EntityName}.{unresolved.ColumnName}", unresolved.Reason));

        // The destination's own input column CACHES the SOURCE's buffer type (e.g. "r8"),
        // independent of the destination's own EXTERNAL (real table) column type resolved.Type
        // already carries (e.g. "i4") -- these normally agree, but an Excel Source's own output
        // is ALWAYS typed r8/wstr regardless of the real intended column type (Excel/ACE OLEDB
        // has no narrower numeric types at all), first hit real 2026-08-28 by RBC_Demo_ETL's own
        // DFT_ExcelImport (CustomerID: r8 from the worksheet, i4 at the destination table). SSIS
        // itself accepts this pairing (the OLE DB provider does its own implicit conversion at
        // insert time); a plain `row.ColumnName` PASSTHROUGH in GENERATED C# does not, and
        // double->int is not an implicit C# conversion -- confirmed as a real CS0266 compile
        // error, not assumed, by actually building a fixture reproducing this exact real
        // pairing.
        // A Flat File Destination has no real narrowing risk at all -- every column it writes is
        // fundamentally text (its own connection manager types EVERY column DT_WSTR regardless of
        // what feeds it, confirmed real from this destination type's own connection-manager
        // builder), unlike an OLE DB Destination's binary/typed columns where a genuine coercion
        // decision (round/truncate/checked) is needed. So a Flat File Destination never GAPS a
        // type mismatch -- it widens via ToString() instead (flatFileStringConversionColumns,
        // consumed by the plain-passthrough case below). Confirmed as a real, previously-latent
        // gap (this check originally reported it, then a raw `row.ID` assignment still failed
        // CS0029 int->string once the gap was merely suppressed) by actually generating and
        // building this exact pairing: a Multicast branch's own int ID column feeding a Flat File
        // Destination.
        // The one evidenced OLE DB/ADO NET numeric pairing (r8 source -> i4 destination) is
        // resolved via SsisFn.NarrowR8ToI4 instead of gapping (2026-08-28) -- measured empirically
        // against a real dtexec run (synthetic-numeric-coercion-tables.sql) rather than guessed:
        // SSIS's own OLE DB Destination rounds to the nearest integer, ties-to-EVEN, exactly
        // matching .NET's own default Math.Round rule. A second evidenced pairing (string source
        // -> int destination, e.g. Package_Exports' own DFT_AdoNetRoundTrip: CustomerID read as
        // wstr,20 from a text-typed staging column, i4 at CustomerExportLog) is resolved via
        // SsisFn.ParseWstrToI4 the same way (2026-08-30) -- measured empirically against a real
        // dtexec run (synthetic-string-to-int-coercion-tables.sql): the string parses as a
        // number then rounds ties-to-even, the same rule, but unlike r8->i4 this pairing's
        // FAILURE mode is ALSO measured (non-numeric/empty/whitespace-only/out-of-range text
        // makes the real probe fail hard), so the generated helper throws to match rather than
        // nulling out. Three more pairings (i8->i4, numeric->i4, r4->i4) were named as candidates
        // with no real evidenced instance anywhere in the tracked portfolio, and measured
        // speculatively 2026-08-30 (synthetic-int-numeric-coercion-tables.sql): a real dtexec run
        // confirmed the SAME round-to-nearest-ties-to-even rule as r8->i4/wstr->i4 for both
        // float-shaped pairings (numeric, r4); i8->i4 has no rounding question at all (both sides
        // are already integers). Any OTHER numeric pairing still gaps rather than guessing the
        // same rule applies -- a wrong guess would silently corrupt data instead of failing loudly.
        var isFlatFileDestination = request.DestinationComponent.ComponentClassId == "Microsoft.FlatFileDestination";
        var inputColumnsByRefId = request.DestinationComponent.Inputs.FirstOrDefault()?.Columns
            .ToDictionary(c => c.RefId) ?? [];
        var mismatchedTypeColumns = new HashSet<string>();
        var flatFileStringConversionColumns = new HashSet<string>();
        var narrowedR8ToI4Columns = new HashSet<string>();
        var narrowedI8ToI4Columns = new HashSet<string>();
        var narrowedNumericToI4Columns = new HashSet<string>();
        var narrowedR4ToI4Columns = new HashSet<string>();
        // Precomputed via the shared detector (not inline) because PackageGenerator's
        // ResolveNullableColumnNames needs the SAME answer before EntityEmitter runs --
        // ParseWstrToI4 always returns int? (string/string? are the same runtime type, unlike
        // double/double?, so there's no non-nullable overload to fall back on), so the
        // destination entity property must already be nullable-inferred by the time
        // EntityEmitter sees it. Computing this twice would risk the two answers drifting apart.
        var parsedWstrToI4Columns = DetectParsedWstrToI4Columns(request.DestinationComponent);
        foreach (var column in resolved.Columns)
        {
            if (column.Type is null) continue;
            if (!inputColumnsByRefId.TryGetValue(column.PipelineColumnRefId, out var inputColumn)) continue;
            var sourceType = SsisPipelineTypeMap.Resolve(inputColumn.CachedDataType);
            if (sourceType is null || sourceType.ClrTypeName == column.Type.ClrTypeName) continue;

            if (isFlatFileDestination)
            {
                flatFileStringConversionColumns.Add(column.PipelineColumnName);
                continue;
            }

            if (sourceType.ClrTypeName == "double" && column.Type.ClrTypeName == "int")
            {
                narrowedR8ToI4Columns.Add(column.PipelineColumnName);
                continue;
            }

            if (sourceType.ClrTypeName == "long" && column.Type.ClrTypeName == "int")
            {
                narrowedI8ToI4Columns.Add(column.PipelineColumnName);
                continue;
            }

            if (sourceType.ClrTypeName == "decimal" && column.Type.ClrTypeName == "int")
            {
                narrowedNumericToI4Columns.Add(column.PipelineColumnName);
                continue;
            }

            if (sourceType.ClrTypeName == "float" && column.Type.ClrTypeName == "int")
            {
                narrowedR4ToI4Columns.Add(column.PipelineColumnName);
                continue;
            }

            if (parsedWstrToI4Columns.Contains(column.PipelineColumnName)) continue;

            mismatchedTypeColumns.Add(column.PipelineColumnName);
            gaps.Add(new GenerationGap($"{request.EntityName}.{column.ExternalColumnName}",
                $"source column '{column.PipelineColumnName}' is buffered as {sourceType.ClrTypeName} but the destination column is {column.Type.ClrTypeName} -- a numeric coercion on a plain passthrough column is not generated yet (SSIS's own OLE DB provider performs this implicitly at insert time; this tool has no evidence for which rounding/truncation rule to reproduce)"));
        }

        // A real, previously-SILENT bug (RBC_Demo_ETL's own Package.dtsx/DFT_LoadCustomers,
        // found 2026-08-30): the "everything not computed/converted is a genuine passthrough"
        // assumption below is only true when the column's actual producer is a real source or a
        // known structural pass-through -- it breaks the moment SOME OTHER pipeline component
        // (a Script Component being the concrete evidenced case: SCR_CleanseCustomerRow adds
        // FullName/CleanEmail/IsValidRow/LoadDateTime, none of which exist on the raw CSV row
        // type) genuinely SYNTHESIZES a new column with that name. The report showed ZERO gaps
        // for this flow; only actually building the generated project surfaced the CS1061s. Not
        // lineage-traced (Sort/Merge/UnionAll's own output columns get a FRESH lineageId,
        // confirmed by reading Package_Transforms.dtsx's own MRG_SortedUnion input->output
        // OutputColumnLineageID reference directly -- tracing lineage back from the destination
        // would misidentify Sort/Merge themselves as "the producer" and wrongly gap every
        // already-working Sort/Merge/UnionAll remerge flow). Instead: any component in this
        // flow's pipeline whose type ISN'T recognized as a real source, a known structural
        // pass-through, or one of the two types already resolved above (Derived Column, Data
        // Conversion) has its own output columns collected here; a destination column matching
        // one of those names by NAME is flagged as a gap instead of guessed at.
        var unrecognizedComponentColumns = new Dictionary<string, PipelineComponentSpec>();
        if (!request.TrustAllPassthroughColumns)
        {
            foreach (var comp in request.Pipeline.Components)
            {
                if (IsRecognizedPassthroughComponent(comp)) continue;
                foreach (var output in comp.Outputs.Where(o => o.IsErrorOut != true))
                    foreach (var col in output.Columns)
                        unrecognizedComponentColumns.TryAdd(col.Name, comp);

                // IN-PLACE MODIFICATION, the structural blind spot this gate was missing. A
                // component that REWRITES an existing column declares no output column for it --
                // it marks its own input column usageType="readWrite" and the column keeps its
                // upstream lineageId. Collecting only output columns above therefore left such a
                // column looking exactly like an untouched passthrough. Any readWrite input
                // column on a component this tool does not understand is now named as a gap.
                // Derived Column is excluded because replacedByName resolves it properly (it is
                // not in RecognizedPassthroughComponentClassIds, so it would otherwise land
                // here); Character Map and anything else unmodelled correctly does land here,
                // because no MapFlags semantics have ever been measured.
                if (comp.ComponentClassId == "Microsoft.DerivedColumn") continue;
                foreach (var input in comp.Inputs)
                    foreach (var col in input.Columns.Where(c => string.Equals(c.UsageType, "readWrite", StringComparison.OrdinalIgnoreCase)))
                        unrecognizedComponentColumns.TryAdd(col.CachedName, comp);
            }
        }

        var assignments = new List<string>();
        // Tier-2 Fill_* declarations (--seams). Empty unless EmitSeams and a Script Component
        // genuinely produces one of this destination's columns.
        var seams = new List<string>();
        foreach (var column in resolved.Columns)
        {
            // A replaced column has the SAME name as the upstream column it rewrites, whereas a
            // computed one introduces a new name, so these two lookups cannot collide.
            if (computedByName.TryGetValue(column.PipelineColumnName, out var derivedColumn) ||
                replacedByName.TryGetValue(column.PipelineColumnName, out derivedColumn))
            {
                var translated = TranslateDerivedColumn(derivedColumn, request.EntityName, column.ExternalColumnName, lineage, columnTypes, request.NullableColumnNames, convertedByName);
                if (translated is NotTranslatable notTranslatable)
                {
                    gaps.Add(new GenerationGap($"{request.EntityName}.{column.ExternalColumnName}", notTranslatable.Reason));
                    continue;
                }

                var expr = ((TranslatedOk)translated).CSharpExpression;
                CollectSsisFunctions(expr, functionsUsed);
                assignments.Add($"        {column.ExternalColumnName} = {expr},");
            }
            else if (convertedByName.TryGetValue(column.PipelineColumnName, out var conversion))
            {
                var converted = TranslateDataConversion(conversion, column.PipelineColumnName, lineage);
                if (converted is NotTranslatable notTranslatable)
                {
                    gaps.Add(new GenerationGap($"{request.EntityName}.{column.ExternalColumnName}", notTranslatable.Reason));
                    continue;
                }

                var expr = ((TranslatedOk)converted).CSharpExpression;
                CollectSsisFunctions(expr, functionsUsed);
                assignments.Add($"        {column.ExternalColumnName} = {expr},");
            }
            else if (request.LookupJoin is { } join
                     && join.OutputToReferenceColumn.TryGetValue(column.PipelineColumnName, out var referenceColumn))
            {
                // Indexer, not TryGetValue: a full-cache Lookup whose no-match output is not
                // routed anywhere FAILS the component on a miss (SSIS's own default), so throwing
                // KeyNotFoundException is the faithful translation. A Lookup that redirects
                // no-match rows is a different, unsupported shape -- PackageGenerator gaps it
                // rather than reaching here.
                assignments.Add($"        {column.ExternalColumnName} = {join.CacheParameterName}[row.{join.InputColumnName}].{referenceColumn},");
            }
            else if (flatFileStringConversionColumns.Contains(column.PipelineColumnName))
            {
                // A nullable-inferred source column needs a null-conditional ToString() (empty
                // string for a genuine NULL) rather than a plain one, which would NullReferenceException.
                var isNullable = request.NullableColumnNames?.Contains(column.PipelineColumnName) == true;
                var expr = isNullable
                    ? $"row.{column.PipelineColumnName}?.ToString() ?? \"\""
                    : $"row.{column.PipelineColumnName}.ToString()";
                assignments.Add($"        {column.ExternalColumnName} = {expr},");
            }
            else if (narrowedR8ToI4Columns.Contains(column.PipelineColumnName))
            {
                // SsisFn.NarrowR8ToI4 has both a double and a double? overload -- the same call
                // shape works whether or not this column is nullable-inferred, unlike the Flat
                // File string-conversion case above (which needs the null-conditional `?`
                // operator itself, not just an overload).
                var expr = $"SsisFn.NarrowR8ToI4(row.{column.PipelineColumnName})";
                CollectSsisFunctions(expr, functionsUsed);
                assignments.Add($"        {column.ExternalColumnName} = {expr},");
            }
            else if (narrowedI8ToI4Columns.Contains(column.PipelineColumnName))
            {
                var expr = $"SsisFn.NarrowI8ToI4(row.{column.PipelineColumnName})";
                CollectSsisFunctions(expr, functionsUsed);
                assignments.Add($"        {column.ExternalColumnName} = {expr},");
            }
            else if (narrowedNumericToI4Columns.Contains(column.PipelineColumnName))
            {
                var expr = $"SsisFn.NarrowNumericToI4(row.{column.PipelineColumnName})";
                CollectSsisFunctions(expr, functionsUsed);
                assignments.Add($"        {column.ExternalColumnName} = {expr},");
            }
            else if (narrowedR4ToI4Columns.Contains(column.PipelineColumnName))
            {
                var expr = $"SsisFn.NarrowR4ToI4(row.{column.PipelineColumnName})";
                CollectSsisFunctions(expr, functionsUsed);
                assignments.Add($"        {column.ExternalColumnName} = {expr},");
            }
            else if (parsedWstrToI4Columns.Contains(column.PipelineColumnName))
            {
                var expr = $"SsisFn.ParseWstrToI4(row.{column.PipelineColumnName})";
                CollectSsisFunctions(expr, functionsUsed);
                assignments.Add($"        {column.ExternalColumnName} = {expr},");
            }
            else if (unrecognizedComponentColumns.TryGetValue(column.PipelineColumnName, out var producer))
            {
                // The destination entity's own property type, resolved by EXACTLY the rule
                // EntityEmitter uses -- a seam whose return type disagreed with the property it
                // feeds would turn a clear CS8795 ("fill this in") into a confusing CS0029.
                var seamIsNullable = request.NullableColumnNames?.Contains(column.PipelineColumnName) ?? false;
                var seamType = column.Type is null
                    ? null
                    : seamIsNullable ? column.Type.ClrTypeName + "?" : column.Type.ClrTypeName;

                if (request.EmitSeams && producer.ScriptComponent is not null && seamType is not null)
                {
                    var methodName = $"Fill_{column.ExternalColumnName}";
                    if (seams.Count > 0) seams.Add("");
                    seams.Add($"    /// <summary>Buffer column '{column.PipelineColumnName}', produced by Script Component");
                    seams.Add($"    /// '{producer.Name}'. Work packet: SCRIPT-COLUMN {request.EntityName}.{column.ExternalColumnName}.</summary>");
                    seams.Add($"    private partial {seamType} {methodName}({request.RowTypeName} row, in RowContext ctx);");
                    assignments.Add($"        {column.ExternalColumnName} = {methodName}(row, ctx),");

                    // Still a BLOCKING gap, and still reported: a seam is outstanding work, not a
                    // resolution. generation-readiness.md must keep saying this package is not
                    // generatable until the fill exists -- which is literally true, it won't build.
                    gaps.Add(new GenerationGap($"{request.EntityName}.{column.ExternalColumnName}",
                        $"column '{column.PipelineColumnName}' is produced by Script Component '{producer.Name}' -- emitted as a `private partial {seamType} {methodName}({request.RowTypeName} row, in RowContext ctx)` seam. The project will not compile (CS8795) until a second part of '{request.TransformClassName}' implements it; apply one with `ssisx apply-fills`.",
                        Kind: GapKind.ScriptComponentColumn,
                        EvidenceRefId: producer.RefId));
                    continue;
                }

                gaps.Add(new GenerationGap($"{request.EntityName}.{column.ExternalColumnName}",
                    $"column '{column.PipelineColumnName}' is produced by '{producer.Name}' ({producer.ComponentClassId}), a component type this tool does not translate -- cannot safely generate a passthrough reference. If this is a Script Component, its transform logic must be ported by hand, the same limitation as a Script Task.",
                    // Classified only when the producer really IS a Script Component: its source is
                    // present in the .dtsx, so this is a portable translation job (Tier 2). Any OTHER
                    // unrecognized producer stays Unclassified -- that is missing tool support, and
                    // hand-patching it per package would hide one systemic emitter gap behind N patches.
                    Kind: producer.ScriptComponent is not null ? GapKind.ScriptComponentColumn : GapKind.Unclassified,
                    EvidenceRefId: producer.ScriptComponent is not null ? producer.RefId : null));
            }
            else if (!mismatchedTypeColumns.Contains(column.PipelineColumnName))
            {
                assignments.Add($"        {column.ExternalColumnName} = row.{column.PipelineColumnName},");
            }
        }

        if (assignments.Count == 0)
        {
            gaps.Add(new GenerationGap(request.TransformClassName, "no destination columns could be generated"));
            return new TransformEmitResult(new EmitResult([], gaps), functionsUsed);
        }

        var lines = BuildFile(request, assignments, functionsUsed, seams);
        var file = new GeneratedFile($"Mapping/{request.TransformClassName}.cs", Rendering.JoinLines(lines));
        return new TransformEmitResult(new EmitResult([file], gaps), functionsUsed);
    }

    private static List<string> BuildFile(TransformRequest request, List<string> assignments, HashSet<string> functionsUsed, List<string> seams)
    {
        var usesWidthGuard = assignments.Any(a => a.Contains("WidthGuard.Wstr("));

        var lines = new List<string>
        {
            "using Etl.Core.Abstractions;",
        };
        if (usesWidthGuard) lines.Add("using Etl.Core.Ssis;");
        lines.Add($"using {request.RowTypeNamespace};");
        lines.Add($"using {request.EntityNamespace};");
        if (functionsUsed.Count > 0) lines.Add($"using {request.SsisFnNamespace};");
        lines.Add("");
        lines.Add($"namespace {request.MappingNamespace};");
        lines.Add("");
        // A Lookup flow's transform takes its preloaded reference cache as a primary-constructor
        // parameter -- Program.cs awaits the load at top level and constructs this directly (see
        // ProgramEmitter's LookupPreload), so there is nothing for DI to resolve here.
        // `partial` only when there is actually a seam to implement -- so a flow with no Script
        // Component keeps byte-identical output to before --seams existed.
        var partial = seams.Count > 0 ? "partial " : "";
        if (seams.Count > 0)
        {
            lines.Add($"/// <summary>Has {seams.Count(s => s.Contains("private partial "))} unimplemented Tier-2 seam(s): column(s) produced by a Script");
            lines.Add("/// Component, whose logic this tool does not translate. Implement each one in a SECOND");
            lines.Add("/// PART of this partial class -- a class part, not just a method body, so the port can");
            lines.Add("/// declare its own fields (a compiled Regex, a lookup table). Until then this project");
            lines.Add("/// deliberately does NOT compile: CS8795, one error per unfilled seam. Put the part in");
            lines.Add($"/// fills/&lt;Package&gt;/{request.TransformClassName}.Fills.cs and run `ssisx apply-fills`.</summary>");
        }
        var declaration = request.LookupJoin is { } join
            ? $"public sealed {partial}class {request.TransformClassName}(Dictionary<{join.KeyClrTypeName}, {join.CacheClassName}.ReferenceRow> {join.CacheParameterName}) : IRowTransform<{request.RowTypeName}, {request.EntityName}>"
            : $"public sealed {partial}class {request.TransformClassName} : IRowTransform<{request.RowTypeName}, {request.EntityName}>";
        lines.Add(declaration);
        lines.Add("{");
        lines.Add($"    public {request.EntityName} Map({request.RowTypeName} row, in RowContext ctx) => new()");
        lines.Add("    {");
        lines.AddRange(assignments);
        lines.Add("    };");
        if (seams.Count > 0)
        {
            lines.Add("");
            lines.AddRange(seams);
        }
        lines.Add("}");
        return lines;
    }

    /// <summary>The three things <see cref="TranslateDerivedColumn"/> actually needs from a
    /// Derived Column column, so that an OUTPUT column (a newly computed column) and a readWrite
    /// INPUT column (a "Replace &lt;column&gt;" in-place rewrite) can share one translation path
    /// instead of the in-place case having none at all.</summary>
    private readonly record struct DerivedExpression(string Name, string RefId, string? Expression, string? FriendlyExpression);

    private static TranslatedExpression TranslateDerivedColumn(
        DerivedExpression derivedColumn, string entityName, string destinationColumnName,
        LineageSpec lineage, Dictionary<string, PipelineOutputColumnSpec> columnTypes,
        IReadOnlySet<string>? nullableColumnNames, Dictionary<string, DataConversionColumnSpec> convertedByName)
    {
        var expressionText = derivedColumn.FriendlyExpression ?? derivedColumn.Expression;
        if (expressionText is null)
            return new NotTranslatable($"'{derivedColumn.Name}' has no Expression/FriendlyExpression to translate");

        ExprNode ast;
        try
        {
            ast = SsisExpression.Parse(expressionText);
        }
        catch (SsisExpressionError ex)
        {
            return new NotTranslatable($"could not parse expression '{expressionText}': {ex.Message}");
        }

        var references = new Dictionary<string, ColumnReference>();
        foreach (var edge in lineage.Edges.Where(e => e.Kind == "ExpressionDerived" && e.ToColumnRefId == derivedColumn.RefId))
        {
            if (references.ContainsKey(edge.FromColumnName)) continue;

            // A reference to a Data Conversion's own output column (e.g. DER_Enrich.TenureDays
            // referencing DCONV_Types.SignupDate_dt) has no row-type property of its own to read
            // -- unlike every other producer here, it's computed inline from its RAW source
            // column via the same SsisFn.ToNullable* helper TranslateDataConversion uses for the
            // direct-to-destination case. Always nullable (IgnoreFailure's own guarantee, see
            // DataConvertPayload's doc comment), independent of nullableColumnNames evidence.
            if (convertedByName.TryGetValue(edge.FromColumnName, out var conversion))
            {
                var convertedExpr = TranslateDataConversion(conversion, edge.FromColumnName, lineage);
                if (convertedExpr is NotTranslatable convertedGap) return convertedGap;

                var convertedType = MapPipelineTypeToSsisType(conversion.TargetDataType)
                    ?? throw new InvalidOperationException($"Data Conversion column '{edge.FromColumnName}' translated successfully but its target type '{conversion.TargetDataType}' has no SsisType mapping -- TranslateDataConversion and MapPipelineTypeToSsisType have drifted out of sync.");
                references[edge.FromColumnName] = new ColumnReference(((TranslatedOk)convertedExpr).CSharpExpression, convertedType, IsNullable: true);
                continue;
            }

            if (!columnTypes.TryGetValue(edge.FromColumnRefId, out var producerColumn))
                return new NotTranslatable($"referenced column '{edge.FromColumnName}' has no resolvable producer in this pipeline");

            var mapped = MapPipelineTypeToSsisType(producerColumn.DataType);
            if (mapped is null)
                return new NotTranslatable($"referenced column '{edge.FromColumnName}' has unmapped pipeline data type '{producerColumn.DataType}'");

            var isNullable = nullableColumnNames?.Contains(edge.FromColumnName) ?? false;
            references[edge.FromColumnName] = new ColumnReference($"row.{edge.FromColumnName}", mapped.Value, isNullable);
        }

        return ExpressionTranslator.TranslateColumn(ast, entityName, destinationColumnName, references);
    }

    /// <summary>
    /// Resolves a Data Conversion column's own RAW source column (via the "DataConversion"
    /// lineage edge <c>LineageBuilder</c> emits -- see that method's own doc comment) and wraps
    /// it in the <c>SsisFn.To*</c> helper matching this generator's empirically-observed
    /// IgnoreFailure semantics (see <see cref="DataConvertPayload"/>'s own doc comment: a
    /// failing/NULL/empty conversion yields NULL, never drops the row). Only the two evidenced
    /// target types are supported -- anything else is a named gap, not a guess, same "gaps not
    /// guesses" rule as everywhere else in this tool. Internal (not private) so RouterEmitter
    /// can reuse it for a Conditional Split condition referencing a Data Conversion column --
    /// same sharing precedent as CollectSsisFunctions/MapPipelineTypeToSsisType below.
    /// </summary>
    internal static TranslatedExpression TranslateDataConversion(
        DataConversionColumnSpec conversion, string outputColumnName, LineageSpec lineage)
    {
        var edge = lineage.Edges.FirstOrDefault(e => e.Kind == "DataConversion" && e.ToColumnName == outputColumnName);
        if (edge is null)
            return new NotTranslatable($"Data Conversion column '{outputColumnName}' has no resolvable source column");

        return WrapDataConversion(conversion, $"row.{edge.FromColumnName}");
    }

    /// <summary>Same wrapping as <see cref="TranslateDataConversion(DataConversionColumnSpec,string,LineageSpec)"/>,
    /// but for a caller that has ALREADY resolved the raw source column expression itself rather
    /// than through a global <see cref="LineageSpec"/> edge search -- needed by Merge Join's own
    /// emitter (<c>MergeJoinProjectionEmitter</c>), where two Data Conversion components in the
    /// SAME pipeline can produce a column with the identical NAME on each side (e.g. both
    /// DCONV_CustId_L/DCONV_CustId_R produce "CustomerID_i4"), which would make the edge-based
    /// lookup above genuinely ambiguous (it matches by bare column name only, with no
    /// per-component scoping) -- Merge Join's planner already resolves each side's own
    /// <see cref="DataConversionColumnSpec"/> unambiguously via <c>MergeJoinSideSource</c>, so
    /// there is no need to re-derive it through the shared, name-only lineage edge search.</summary>
    internal static TranslatedExpression TranslateDataConversionFromRawExpression(DataConversionColumnSpec conversion, string rawColumnExpression) =>
        WrapDataConversion(conversion, rawColumnExpression);

    private static TranslatedExpression WrapDataConversion(DataConversionColumnSpec conversion, string sourceRef) =>
        conversion.TargetDataType?.ToLowerInvariant() switch
        {
            "i4" => new TranslatedOk($"SsisFn.ToNullableI4({sourceRef})"),
            "dbdate" => new TranslatedOk($"SsisFn.ToNullableDate({sourceRef})"),
            "r8" => new TranslatedOk($"SsisFn.ToNullableR8({sourceRef})"),
            "bool" => new TranslatedOk($"SsisFn.ToNullableBool({sourceRef})"),
            // i2/i8/dbtimestamp built speculatively 2026-08-30 (synthetic-data-conversion-types2-
            // tables.sql) -- no real package needs any of the three yet; measured via a real
            // dtexec run to follow the same "NULL on any failure" convention as i4/dbdate/r8/bool.
            "i2" => new TranslatedOk($"SsisFn.ToNullableI2({sourceRef})"),
            "i8" => new TranslatedOk($"SsisFn.ToNullableI8({sourceRef})"),
            "dbtimestamp" => new TranslatedOk($"SsisFn.ToNullableDateTime({sourceRef})"),
            // DT_WSTR is a genuinely different shape from every other target above -- measured
            // against a real dtexec run (synthetic-data-conversion-types-tables.sql), an overlong
            // source string TRUNCATES to fit under IgnoreFailure rather than nulling out, so the
            // helper needs the declared target width, not just the raw value. No declared Length
            // is a named gap, never a guessed default width.
            "wstr" => conversion.Length is int maxLength
                ? new TranslatedOk($"SsisFn.ToWstr({sourceRef}, {maxLength})")
                : new NotTranslatable($"Data Conversion to DT_WSTR has no declared target width to truncate to"),
            _ => new NotTranslatable($"Data Conversion to target type '{conversion.TargetDataType}' is not supported yet"),
        };

    /// <summary>Component types whose own output columns are safe to assume an unresolved
    /// destination column can reference via a plain `row.ColumnName` passthrough -- real
    /// sources, recognized structural pass-throughs (a component that reorders/routes/merges
    /// rows without ever synthesizing a genuinely NEW value), and the two destination shapes.
    /// Derived Column and Data Conversion are deliberately NOT here -- those are resolved
    /// explicitly above (computedByName/convertedByName), not via this passthrough gate.
    /// Anything NOT in this set (a Script Component being the concrete real-evidenced case --
    /// see Emit's own call site -- but this also covers Aggregate/Lookup/any future unmodeled
    /// type) is treated as unsafe to guess at.</summary>
    private static readonly HashSet<string> RecognizedPassthroughComponentClassIds =
    [
        "Microsoft.FlatFileSource",
        "Microsoft.ExcelSource",
        "Microsoft.Sort",
        "Microsoft.Merge",
        "Microsoft.UnionAll",
        "Microsoft.RowCount",
        "Microsoft.ConditionalSplit",
        "Microsoft.Multicast",
        "Microsoft.MergeJoin",
        "Microsoft.OLEDBCommand",
        "Microsoft.OLEDBDestination",
        "Microsoft.FlatFileDestination",
    ];

    private static bool IsRecognizedPassthroughComponent(PipelineComponentSpec component) =>
        RecognizedPassthroughComponentClassIds.Contains(component.ComponentClassId)
        || SourceInfo.IsSqlSource(component)
        || component.AdoNetDestination is not null;

    /// <summary>Internal (not private) so PackageGenerator's ResolveNullableColumnNames can
    /// detect the same string(wstr)-source/int(i4)-destination passthrough mismatch BEFORE
    /// EntityEmitter runs -- see this method's own call site in Emit for why the entity property
    /// must be nullable-inferred structurally rather than by ISNULL evidence.</summary>
    internal static HashSet<string> DetectParsedWstrToI4Columns(PipelineComponentSpec destinationComponent)
    {
        var result = new HashSet<string>();
        var resolved = PipelineResolver.ResolveDestinationInput(destinationComponent);
        var inputColumnsByRefId = destinationComponent.Inputs.FirstOrDefault()?.Columns
            .ToDictionary(c => c.RefId) ?? [];
        foreach (var column in resolved.Columns)
        {
            if (column.Type is null) continue;
            if (!inputColumnsByRefId.TryGetValue(column.PipelineColumnRefId, out var inputColumn)) continue;
            var sourceType = SsisPipelineTypeMap.Resolve(inputColumn.CachedDataType);
            if (sourceType?.ClrTypeName == "string" && column.Type.ClrTypeName == "int")
                result.Add(column.PipelineColumnName);
        }
        return result;
    }

    /// <summary>Internal (not private) so RouterEmitter can reuse the exact same SsisFn.*
    /// detection for Conditional Split condition translation instead of duplicating it -- same
    /// sharing precedent as MapPipelineTypeToSsisType below.</summary>
    internal static void CollectSsisFunctions(string csharpExpression, HashSet<string> functionsUsed)
    {
        if (csharpExpression.Contains("SsisFn.Upper(")) functionsUsed.Add("Upper");
        if (csharpExpression.Contains("SsisFn.Trim(")) functionsUsed.Add("Trim");
        if (csharpExpression.Contains("SsisFn.Substring(")) functionsUsed.Add("Substring");
        if (csharpExpression.Contains("SsisFn.FindString(")) functionsUsed.Add("FindString");
        if (csharpExpression.Contains("SsisFn.DateDiffDays(")) functionsUsed.Add("DateDiffDays");
        if (csharpExpression.Contains("SsisFn.Str(")) functionsUsed.Add("Str");
        if (csharpExpression.Contains("SsisFn.ToNullableI4(")) functionsUsed.Add("ToNullableI4");
        if (csharpExpression.Contains("SsisFn.ToNullableDate(")) functionsUsed.Add("ToNullableDate");
        if (csharpExpression.Contains("SsisFn.NarrowR8ToI4(")) functionsUsed.Add("NarrowR8ToI4");
        if (csharpExpression.Contains("SsisFn.NarrowI8ToI4(")) functionsUsed.Add("NarrowI8ToI4");
        if (csharpExpression.Contains("SsisFn.NarrowNumericToI4(")) functionsUsed.Add("NarrowNumericToI4");
        if (csharpExpression.Contains("SsisFn.NarrowR4ToI4(")) functionsUsed.Add("NarrowR4ToI4");
        if (csharpExpression.Contains("SsisFn.ParseWstrToI4(")) functionsUsed.Add("ParseWstrToI4");
        if (csharpExpression.Contains("SsisFn.ToNullableR8(")) functionsUsed.Add("ToNullableR8");
        if (csharpExpression.Contains("SsisFn.ToNullableBool(")) functionsUsed.Add("ToNullableBool");
        if (csharpExpression.Contains("SsisFn.ToWstr(")) functionsUsed.Add("ToWstr");
        if (csharpExpression.Contains("SsisFn.ToNullableI2(")) functionsUsed.Add("ToNullableI2");
        if (csharpExpression.Contains("SsisFn.ToNullableI8(")) functionsUsed.Add("ToNullableI8");
        if (csharpExpression.Contains("SsisFn.ToNullableDateTime(")) functionsUsed.Add("ToNullableDateTime");
    }

    /// <summary>Same evidenced surface as SsisPipelineTypeMap, mapped into the expression
    /// evaluator's own (smaller, oracle-pinned) SsisType enum instead -- the two are
    /// deliberately separate type spaces, see SsisPipelineTypeMap's own doc comment for why.
    /// Internal (not private) so RouterEmitter can reuse the exact same buffer-type mapping for
    /// Conditional Split condition translation instead of duplicating this switch.</summary>
    internal static SsisType? MapPipelineTypeToSsisType(string? dataType) => dataType?.ToLowerInvariant() switch
    {
        "wstr" or "str" => SsisType.WStr,
        "i2" => SsisType.I2,
        "i4" => SsisType.I4,
        "i8" => SsisType.I8,
        "r4" => SsisType.R4,
        "r8" => SsisType.R8,
        "numeric" or "decimal" or "currency" => SsisType.Numeric,
        "bool" => SsisType.Bool,
        "dbtimestamp" or "dbtimestamp2" or "dbtimestampoffset" => SsisType.DbTimeStamp,
        "dbdate" => SsisType.DbDate,
        _ => null,
    };

    private static Dictionary<string, PipelineOutputColumnSpec> BuildColumnTypeLookup(PipelineSpec pipeline)
    {
        var map = new Dictionary<string, PipelineOutputColumnSpec>();
        foreach (var component in pipeline.Components)
            foreach (var output in component.Outputs)
                foreach (var column in output.Columns)
                    map.TryAdd(column.RefId, column);
        return map;
    }
}
