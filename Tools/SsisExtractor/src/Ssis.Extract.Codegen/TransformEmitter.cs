using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;
using Ssis.Runtime.Expressions;

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
    IReadOnlyList<PipelineComponentSpec>? DataConversions = null);

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

        var computedByName = new Dictionary<string, PipelineOutputColumnSpec>();
        foreach (var derivedColumn in request.DerivedColumns)
        {
            foreach (var column in derivedColumn.Outputs
                .Where(o => o.IsErrorOut != true)
                .SelectMany(o => o.Columns)
                .Where(c => c.Expression is not null))
            {
                computedByName.TryAdd(column.Name, column);
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
        // pairing. Reported as a gap rather than guessing a cast: this tool has no oracle
        // evidence for which numeric coercion (truncate/round/checked) the real OLE DB provider
        // performs, and a wrong guess would silently corrupt data instead of failing loudly.
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
        var isFlatFileDestination = request.DestinationComponent.ComponentClassId == "Microsoft.FlatFileDestination";
        var inputColumnsByRefId = request.DestinationComponent.Inputs.FirstOrDefault()?.Columns
            .ToDictionary(c => c.RefId) ?? [];
        var mismatchedTypeColumns = new HashSet<string>();
        var flatFileStringConversionColumns = new HashSet<string>();
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

            mismatchedTypeColumns.Add(column.PipelineColumnName);
            gaps.Add(new GenerationGap($"{request.EntityName}.{column.ExternalColumnName}",
                $"source column '{column.PipelineColumnName}' is buffered as {sourceType.ClrTypeName} but the destination column is {column.Type.ClrTypeName} -- a numeric coercion on a plain passthrough column is not generated yet (SSIS's own OLE DB provider performs this implicitly at insert time; this tool has no evidence for which rounding/truncation rule to reproduce)"));
        }

        var assignments = new List<string>();
        foreach (var column in resolved.Columns)
        {
            if (computedByName.TryGetValue(column.PipelineColumnName, out var derivedColumn))
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

        var lines = BuildFile(request, assignments, functionsUsed);
        var file = new GeneratedFile($"Mapping/{request.TransformClassName}.cs", Rendering.JoinLines(lines));
        return new TransformEmitResult(new EmitResult([file], gaps), functionsUsed);
    }

    private static List<string> BuildFile(TransformRequest request, List<string> assignments, HashSet<string> functionsUsed)
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
        lines.Add($"public sealed class {request.TransformClassName} : IRowTransform<{request.RowTypeName}, {request.EntityName}>");
        lines.Add("{");
        lines.Add($"    public {request.EntityName} Map({request.RowTypeName} row, in RowContext ctx) => new()");
        lines.Add("    {");
        lines.AddRange(assignments);
        lines.Add("    };");
        lines.Add("}");
        return lines;
    }

    private static TranslatedExpression TranslateDerivedColumn(
        PipelineOutputColumnSpec derivedColumn, string entityName, string destinationColumnName,
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
            _ => new NotTranslatable($"Data Conversion to target type '{conversion.TargetDataType}' is not supported yet"),
        };

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
