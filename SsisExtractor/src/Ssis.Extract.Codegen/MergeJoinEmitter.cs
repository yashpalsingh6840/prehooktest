using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits a Merge Join flow's remaining pieces once <see cref="PackagePlanner.PlanMergeJoin"/> has
/// resolved both sides: the COMBINED row type (one property per Merge Join output column -- the
/// shape a plain destination transform already knows how to consume via <c>row.ColumnName</c>,
/// exactly like a SQL/CSV row) and a static mapper class providing the join KEY selectors plus
/// the projection from (Left row, Right row) to the combined row.
///
/// <b>Why this needs its own emitter rather than reusing SqlRowEmitter.</b> A Merge Join's own
/// output carries NO external-metadata columns at all (confirmed real: RBC_Demo_ETL's own
/// <c>MRG_CustomerContacts.Outputs[Merge Join Output]</c> has an empty <c>&lt;externalMetadataColumns/&gt;</c>
/// -- unlike a source reading a file/table, a pure pipeline transform has no design-time external
/// schema to join against), so <c>PipelineResolver.Resolve</c> (built around exactly that join)
/// cannot be reused here; column type comes straight from the output column's own
/// dataType/length/precision/scale/codePage instead.
///
/// <b>Resolving what a Sort output column's own VALUE actually is</b> (shared by every Merge
/// Join output column AND the join key itself, see <see cref="ResolveSortOutputValue"/>) walks
/// two hops backward, by hand -- not via <c>Ssis.Extract.Dtsx.LineageBuilder</c>'s own global,
/// name-only edge search, which would be genuinely ambiguous here: two Data Conversion
/// components in the same pipeline can produce a column with the identical name (e.g. both
/// DCONV_CustId_L/R produce "CustomerID_i4"). A Sort output column's own <c>SortColumnId</c>
/// custom property names either the raw source column directly, or (if the plan resolved a
/// <see cref="MergeJoinSideSource.DataConversion"/> for that side) that Data Conversion's own
/// output column -- resolved unambiguously because the plan already scoped exactly which
/// Data Conversion belongs to which side.
/// </summary>
public static class MergeJoinEmitter
{
    public sealed record MergeJoinEmitResult(
        List<GeneratedFile> Files,
        List<GenerationGap> Gaps,
        HashSet<string> SsisFunctionsUsed,
        string RowTypeName,
        string MapperClassName,
        string KeyClrType,
        HashSet<string> NullableColumnNames,
        // Populated only on a successful Emit (the three early-return failure paths below leave
        // these at their defaults) -- everything ComponentTestEmitter.EmitMergeJoinMapperTest
        // needs to synthesize a representative Left/Right row and predict LeftKey/RightKey/Map's
        // own output, without re-deriving any of this resolution a second time.
        IReadOnlyList<MergeJoinTestColumn>? TestColumns = null,
        MergeJoinTestKey? LeftKeyTest = null,
        MergeJoinTestKey? RightKeyTest = null);

    /// <summary>One combined-row output column's test-facing facts, captured alongside the
    /// ordinary resolution in <see cref="Emit"/>. <see cref="RawColumnClrType"/> is only
    /// meaningful when <see cref="ConversionTargetType"/> is null (a plain passthrough) -- a Data
    /// Conversion's own raw source column is ALWAYS synthesized as a string by a starter test
    /// (every <c>SsisFn.ToNullable*</c> helper this tool emits takes a bare <c>string?</c>,
    /// confirmed from <see cref="TransformEmitter.WrapDataConversion"/>'s own switch), regardless
    /// of what the raw column's own declared pipeline type says.</summary>
    public sealed record MergeJoinTestColumn(
        string Name, bool MayBeAbsent, string RawColumnName, string RawColumnClrType,
        string? ConversionTargetType, int? ConversionLength);

    /// <summary>One side's own join-key column -- same shape as <see cref="MergeJoinTestColumn"/>
    /// minus the combined-row column name (a key selector has no output column of its own to
    /// name).</summary>
    public sealed record MergeJoinTestKey(string RawColumnName, string RawColumnClrType, string? ConversionTargetType, int? ConversionLength);

    private sealed record ResolvedValue(
        string Expression, SsisPipelineType Type, bool IsConversionDerived,
        string RawColumnName, string? ConversionTargetType, int? ConversionLength);

    private sealed record ResolvedOutputColumn(string Name, SsisPipelineType Type, bool Nullable, string ValueExpression);

    public static MergeJoinEmitResult Emit(
        string mappingNamespace, string rowTypeNamespace, string ssisFnNamespace,
        string rowTypeName, string mapperClassName,
        string leftRowTypeNamespace, string leftRowTypeName,
        string rightRowTypeNamespace, string rightRowTypeName,
        MergeJoinPlan plan)
    {
        var gaps = new List<GenerationGap>();
        var functionsUsed = new HashSet<string>();
        var mainOutput = plan.Component.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (mainOutput is null)
        {
            gaps.Add(new GenerationGap(rowTypeName, $"Merge Join '{plan.Component.Name}' has no non-error output"));
            return new MergeJoinEmitResult([], gaps, functionsUsed, rowTypeName, mapperClassName, "object", []);
        }

        var resolvedColumns = new List<ResolvedOutputColumn>();
        var testColumns = new List<MergeJoinTestColumn>();
        foreach (var outputCol in mainOutput.Columns)
        {
            var mjCol = plan.Component.MergeJoin?.OutputColumns.FirstOrDefault(c => c.OutputColumnName == outputCol.Name);
            if (mjCol is null)
            {
                gaps.Add(new GenerationGap($"{rowTypeName}.{outputCol.Name}", "Merge Join output column has no resolvable InputColumnID -- not supported"));
                continue;
            }

            var side = mjCol.Side == "Left" ? plan.Left : plan.Right;
            var sidePrefix = mjCol.Side == "Left" ? "left" : "right";

            var sortOutputCol = side.Sort.Outputs.SelectMany(o => o.Columns).FirstOrDefault(c => c.LineageId == mjCol.SourceColumnLineageId);
            if (sortOutputCol is null)
            {
                gaps.Add(new GenerationGap($"{rowTypeName}.{outputCol.Name}", $"could not resolve Sort '{side.Sort.Name}''s own output column for Merge Join column '{outputCol.Name}'"));
                continue;
            }

            // Right side of a LeftOuter join can be entirely absent for an unmatched left row --
            // nullable, and every access to it must be null-guarded, regardless of what produces
            // the value (evidenced LeftOuter semantics: Left is never absent). The key selector
            // lambdas resolved below need no such guard: they receive an actual, already-buffered
            // row instance, never null, by construction (see ResolveSideKey's own doc comment).
            var isRightSide = mjCol.Side == "Right";
            var value = ResolveSortOutputValue(sortOutputCol, side, sidePrefix, sourceParamIsNullable: true, mayBeAbsent: isRightSide, functionsUsed, gaps, $"{rowTypeName}.{outputCol.Name}");
            if (value is null) continue; // ResolveSortOutputValue already added the gap

            // A Data Conversion result is ALWAYS nullable too (IgnoreFailure's own guarantee,
            // same rule TransformEmitter's own Data Conversion handling already establishes).
            resolvedColumns.Add(new ResolvedOutputColumn(outputCol.Name, value.Type, isRightSide || value.IsConversionDerived, value.Expression));
            testColumns.Add(new MergeJoinTestColumn(
                outputCol.Name, MayBeAbsent: isRightSide, value.RawColumnName, value.Type.ClrTypeName,
                value.ConversionTargetType, value.ConversionLength));
        }

        if (resolvedColumns.Count == 0)
        {
            gaps.Add(new GenerationGap(rowTypeName, "no Merge Join output columns could be resolved"));
            return new MergeJoinEmitResult([], gaps, functionsUsed, rowTypeName, mapperClassName, "object", []);
        }

        var leftKey = ResolveSideKey(plan.Left, "left", functionsUsed, gaps, $"{mapperClassName}.LeftKey");
        var rightKey = ResolveSideKey(plan.Right, "right", functionsUsed, gaps, $"{mapperClassName}.RightKey");
        if (leftKey is null || rightKey is null)
            return new MergeJoinEmitResult([], gaps, functionsUsed, rowTypeName, mapperClassName, "object", []);

        var files = new List<GeneratedFile>
        {
            BuildRowTypeFile(rowTypeNamespace, rowTypeName, resolvedColumns),
            BuildMapperFile(mappingNamespace, ssisFnNamespace, mapperClassName, rowTypeNamespace, rowTypeName,
                leftRowTypeNamespace, leftRowTypeName, rightRowTypeNamespace, rightRowTypeName, resolvedColumns, leftKey, rightKey, functionsUsed),
        };

        // The DESTINATION's own entity property must be nullable wherever this row type's own
        // property is -- EntityEmitter needs this same set (keyed by PIPELINE column name, which
        // for a Merge Join flow's destination equals this row type's own property name, the same
        // "row.ColumnName" passthrough convention every other flow already uses) or a plain
        // passthrough assignment (e.g. "CustomerID = row.CustomerID_i4") fails to compile when
        // only one side is nullable.
        var nullableColumnNames = resolvedColumns.Where(c => c.Nullable).Select(c => c.Name).ToHashSet();

        var leftKeyTest = new MergeJoinTestKey(leftKey.RawColumnName, leftKey.Type.ClrTypeName, leftKey.ConversionTargetType, leftKey.ConversionLength);
        var rightKeyTest = new MergeJoinTestKey(rightKey.RawColumnName, rightKey.Type.ClrTypeName, rightKey.ConversionTargetType, rightKey.ConversionLength);

        return new MergeJoinEmitResult(files, gaps, functionsUsed, rowTypeName, mapperClassName, leftKey.Type.ClrTypeName + "?", nullableColumnNames,
            TestColumns: testColumns, LeftKeyTest: leftKeyTest, RightKeyTest: rightKeyTest);
    }

    /// <summary>Resolves a Sort component's OWN output column back to either a plain raw source
    /// column reference or a Data-Conversion-wrapped one -- shared by every Merge Join output
    /// column (via <see cref="MergeJoinOutputColumnSpec.SourceColumnLineageId"/>, which names a
    /// Sort output column by lineageId) and the join key itself (via
    /// <see cref="SortKeySpec.ColumnName"/>, which names one by plain name -- both resolve
    /// through here once the caller has found the right <see cref="PipelineOutputColumnSpec"/>).
    ///
    /// <paramref name="sourceParamIsNullable"/>/<paramref name="mayBeAbsent"/> control how
    /// <paramref name="sidePrefix"/> is accessed, and both matter independently: the join key
    /// selectors (<c>LeftKey(TLeft left)</c>/<c>RightKey(TRight right)</c>) take a NON-nullable
    /// parameter (<paramref name="sourceParamIsNullable"/>: false -- a key selector only ever
    /// runs against a real, already-buffered row), while the projection
    /// (<c>Map(TLeft? left, TRight? right)</c>) takes NULLABLE parameters for BOTH sides even
    /// though only the Right one can genuinely be absent for the one evidenced join type
    /// (LeftOuter) -- so a Left-side access there still needs null-forgiving (<c>!.</c>), not
    /// because Left can truly be null, but because the C# TYPE says it can and
    /// <c>&lt;Nullable&gt;enable&lt;/Nullable&gt;</c> + TreatWarningsAsErrors makes an unguarded
    /// dereference a build error, not just a warning. Caught by actually building the generated
    /// project, not by unit tests alone -- the exact same "verify by running it" discipline as
    /// every other nullability fix in this tool.</summary>
    private static ResolvedValue? ResolveSortOutputValue(
        PipelineOutputColumnSpec sortOutputCol, MergeJoinSideSource side, string sidePrefix,
        bool sourceParamIsNullable, bool mayBeAbsent,
        HashSet<string> functionsUsed, List<GenerationGap> gaps, string gapLocation)
    {
        var upstreamLineageId = StripRef(GetProp(sortOutputCol.Properties, "SortColumnId"));
        if (upstreamLineageId is null)
        {
            gaps.Add(new GenerationGap(gapLocation, $"Sort '{side.Sort.Name}''s own output column '{sortOutputCol.Name}' has no SortColumnId"));
            return null;
        }

        // Non-nullable (key selector): plain access. Nullable-but-guaranteed-present (Left side
        // of a Map projection): null-forgiving. Nullable-and-may-be-absent (Right side of a Map
        // projection): plain access too -- the caller wraps the WHOLE resulting expression in a
        // "sidePrefix is not null ? ... : null"/"sidePrefix?...." guard instead, which narrows
        // sidePrefix for everything inside it, so no "!" is needed at the point of access itself.
        var accessPrefix = !sourceParamIsNullable || mayBeAbsent ? sidePrefix : sidePrefix + "!";

        if (side.DataConversion is not null &&
            side.DataConversion.Outputs.SelectMany(o => o.Columns).FirstOrDefault(c => c.LineageId == upstreamLineageId) is { } conversionOutputCol)
        {
            var conversion = side.DataConversion.DataConvert?.Columns.FirstOrDefault(c => c.OutputColumnName == conversionOutputCol.Name);
            var rawColumn = conversion?.SourceColumnLineageId is null ? null
                : side.SourceComponent.Outputs.SelectMany(o => o.Columns).FirstOrDefault(c => c.LineageId == conversion.SourceColumnLineageId);
            var conversionType = conversion?.TargetDataType is null ? null : SsisPipelineTypeMap.Resolve(conversion.TargetDataType);
            if (conversion is null || rawColumn is null || conversionType is null)
            {
                gaps.Add(new GenerationGap(gapLocation, $"could not resolve Data Conversion '{side.DataConversion.Name}''s own raw source column or target type"));
                return null;
            }

            var translated = TransformEmitter.TranslateDataConversionFromRawExpression(conversion, $"{accessPrefix}.{rawColumn.Name}");
            if (translated is NotTranslatable notTranslatable)
            {
                gaps.Add(new GenerationGap(gapLocation, notTranslatable.Reason));
                return null;
            }

            var expr = ((TranslatedOk)translated).CSharpExpression;
            TransformEmitter.CollectSsisFunctions(expr, functionsUsed);
            return new ResolvedValue(mayBeAbsent ? $"({sidePrefix} is not null ? {expr} : null)" : expr, conversionType, IsConversionDerived: true,
                RawColumnName: rawColumn.Name, ConversionTargetType: conversion.TargetDataType, ConversionLength: conversion.Length);
        }

        var rawPassthroughColumn = side.SourceComponent.Outputs.SelectMany(o => o.Columns).FirstOrDefault(c => c.LineageId == upstreamLineageId);
        var passthroughType = rawPassthroughColumn?.DataType is null ? null : SsisPipelineTypeMap.Resolve(rawPassthroughColumn.DataType);
        if (rawPassthroughColumn is null || passthroughType is null)
        {
            gaps.Add(new GenerationGap(gapLocation, "could not resolve a raw source column (unmapped pipeline data type or unresolved reference)"));
            return null;
        }

        var passthroughExpr = mayBeAbsent ? $"{sidePrefix}?.{rawPassthroughColumn.Name}" : $"{accessPrefix}.{rawPassthroughColumn.Name}";
        return new ResolvedValue(passthroughExpr, passthroughType, IsConversionDerived: false,
            RawColumnName: rawPassthroughColumn.Name, ConversionTargetType: null, ConversionLength: null);
    }

    /// <summary>Resolves one side's own join-key column (NumKeyColumns=1 evidenced -- only the
    /// first key is supported, matching every other raw-value-kept-not-guessed enum in this
    /// tool) to a plain, unguarded value expression -- a key selector lambda always receives a
    /// real, already-buffered row through a NON-nullable parameter, never null.</summary>
    private static ResolvedValue? ResolveSideKey(MergeJoinSideSource side, string sidePrefix, HashSet<string> functionsUsed, List<GenerationGap> gaps, string gapLocation)
    {
        var keySpec = side.Sort.Sort?.Keys.FirstOrDefault();
        if (keySpec is null)
        {
            gaps.Add(new GenerationGap(gapLocation, $"Sort '{side.Sort.Name}' has no recognizable sort key -- not supported"));
            return null;
        }

        var sortOutputCol = side.Sort.Outputs.SelectMany(o => o.Columns).FirstOrDefault(c => c.Name == keySpec.ColumnName);
        if (sortOutputCol is null)
        {
            gaps.Add(new GenerationGap(gapLocation, $"Sort '{side.Sort.Name}''s own key column '{keySpec.ColumnName}' was not found among its output columns"));
            return null;
        }

        return ResolveSortOutputValue(sortOutputCol, side, sidePrefix, sourceParamIsNullable: false, mayBeAbsent: false, functionsUsed, gaps, gapLocation);
    }

    private static GeneratedFile BuildRowTypeFile(string ns, string rowTypeName, List<ResolvedOutputColumn> columns)
    {
        var lines = new List<string> { $"namespace {ns};", "", $"public sealed class {rowTypeName}", "{" };
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0) lines.Add("");
            var c = columns[i];
            var typeName = c.Nullable ? c.Type.ClrTypeName + "?" : c.Type.ClrTypeName;
            var initializer = !c.Nullable && c.Type.ClrTypeName == "string" ? " = \"\";" : "";
            lines.Add($"    public {typeName} {c.Name} {{ get; set; }}{initializer}");
        }
        lines.Add("}");
        return new GeneratedFile($"Sql/{rowTypeName}.cs", Rendering.JoinLines(lines));
    }

    private static GeneratedFile BuildMapperFile(
        string mappingNamespace, string ssisFnNamespace, string mapperClassName, string rowTypeNamespace, string rowTypeName,
        string leftRowTypeNamespace, string leftRowTypeName, string rightRowTypeNamespace, string rightRowTypeName,
        List<ResolvedOutputColumn> columns, ResolvedValue leftKey, ResolvedValue rightKey, HashSet<string> functionsUsed)
    {
        var keyClrType = leftKey.Type.ClrTypeName + "?";
        // Left/Right can independently be CSV- or SQL-sourced, so their own namespaces can
        // differ from each other AND from the combined row type's own -- deduplicated since two
        // (or all three) commonly coincide (e.g. both sides SQL-sourced).
        var usingNamespaces = new List<string> { rowTypeNamespace, leftRowTypeNamespace, rightRowTypeNamespace }.Distinct();
        var lines = usingNamespaces.Select(ns => $"using {ns};").ToList();
        if (functionsUsed.Count > 0) lines.Add($"using {ssisFnNamespace};");
        lines.Add("");
        lines.Add($"namespace {mappingNamespace};");
        lines.Add("");
        lines.Add($"public static class {mapperClassName}");
        lines.Add("{");
        lines.Add($"    public static {keyClrType} LeftKey({leftRowTypeName} left) => {leftKey.Expression};");
        lines.Add("");
        lines.Add($"    public static {keyClrType} RightKey({rightRowTypeName} right) => {rightKey.Expression};");
        lines.Add("");
        lines.Add($"    public static {rowTypeName} Map({leftRowTypeName}? left, {rightRowTypeName}? right) => new()");
        lines.Add("    {");
        for (var i = 0; i < columns.Count; i++)
        {
            var c = columns[i];
            var comma = i < columns.Count - 1 ? "," : "";
            lines.Add($"        {c.Name} = {c.ValueExpression}{comma}");
        }
        lines.Add("    };");
        lines.Add("}");
        return new GeneratedFile($"Mapping/{mapperClassName}.cs", Rendering.JoinLines(lines));
    }

    private static string? GetProp(List<PipelinePropertySpec> properties, string name) =>
        properties.FirstOrDefault(p => p.Name == name)?.Value is { Length: > 0 } v ? v : null;

    private static string? StripRef(string? value) =>
        value is not null && value.StartsWith("#{", StringComparison.Ordinal) && value.EndsWith('}')
            ? value[2..^1]
            : value;
}
