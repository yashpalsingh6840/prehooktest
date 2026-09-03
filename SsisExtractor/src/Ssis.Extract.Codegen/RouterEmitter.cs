using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Pipeline;
using Ssis.Runtime.Expressions;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits one Conditional Split's routing decision, e.g. LoadX.Mapping.CustomerSplitRouter --
/// an IRowRouter&lt;TRow&gt; implementation, an if/else-if chain in EvaluationOrder ending in the
/// default branch's index.
///
/// References are resolved directly from the Conditional Split component's OWN single input
/// column list (<c>CachedName</c>/<c>CachedDataType</c>) rather than via LineageBuilder --
/// unlike TransformEmitter's Derived Column translation (which resolves a computed VALUE from
/// upstream columns feeding it), a routing condition runs on the flow's own source row exactly
/// as the Conditional Split sees it, and CachedName/CachedDataType are that buffer's own
/// column identity at this point, already resolved by the reader with no join needed. This is
/// also the source of scope decision 1 (CLAUDE.md, "Three new component types"): a case whose
/// FriendlyExpression references a value only a Derived Column computes (never a real, named
/// buffer column at the Conditional Split's own input) has no entry in this dictionary and
/// degrades to a translation gap rather than being guessed.
/// </summary>
public static class RouterEmitter
{
    /// <summary>Mirrors TransformEmitter.TransformEmitResult -- a condition can reference an
    /// SsisFn.* helper (e.g. FINDSTRING/TRIM) exactly like a Derived Column expression can, so
    /// the caller needs the same "which functions did this emit actually use" signal to pass
    /// through to SsisFnEmitter.</summary>
    public sealed record RouterEmitResult(EmitResult Result, IReadOnlySet<string> SsisFunctionsUsed);

    public static RouterEmitResult Emit(
        string ns, string routerClassName, string rowTypeNamespace, string rowTypeName, string ssisFnNamespace,
        ConditionalSplitPlan split, PipelineSpec? pipeline = null, PipelineComponentSpec? dataConversion = null)
    {
        var splitComponent = split.Component;
        if (splitComponent.Inputs.Count != 1)
        {
            return new RouterEmitResult(new EmitResult([], [new GenerationGap(routerClassName,
                $"Conditional Split '{splitComponent.Name}' has {splitComponent.Inputs.Count} input(s), expected exactly 1")]), new HashSet<string>());
        }

        // A Conditional Split input column produced by a Data Conversion component (e.g.
        // RBC_Demo_ETL's CSPLIT_Validity referencing CustomerID_i4) has no row-type property of
        // its own -- same "computed inline from its RAW source column" shape TransformEmitter's
        // own TranslateDerivedColumn resolves for a Derived Column referencing the same kind of
        // column, reusing that exact translation rather than duplicating it. Always nullable
        // (IgnoreFailure's own guarantee), independent of any ISNULL(x) usage evidence --
        // unlike NullabilityInference (which stays scoped to Derived Column expressions, see its
        // own doc comment), this recognition is structural, not usage-based, so it needs no
        // separate "Conditional Split condition nullability" inference step at all.
        var convertedByName = new Dictionary<string, DataConversionColumnSpec>();
        foreach (var column in dataConversion?.DataConvert?.Columns ?? [])
            convertedByName.TryAdd(column.OutputColumnName, column);
        var lineage = pipeline is null ? null : LineageBuilder.Build(pipeline);

        var references = new Dictionary<string, ColumnReference>();
        foreach (var inputColumn in splitComponent.Inputs[0].Columns)
        {
            if (lineage is not null && convertedByName.TryGetValue(inputColumn.CachedName, out var conversion))
            {
                var convertedExpr = TransformEmitter.TranslateDataConversion(conversion, inputColumn.CachedName, lineage);
                if (convertedExpr is TranslatedOk ok)
                {
                    var convertedType = TransformEmitter.MapPipelineTypeToSsisType(conversion.TargetDataType);
                    if (convertedType is not null)
                    {
                        references[inputColumn.CachedName] = new ColumnReference(ok.CSharpExpression, convertedType.Value, IsNullable: true);
                        continue;
                    }
                }
                // Unresolvable Data Conversion column -- fall through to the plain mapping below
                // rather than failing the whole router; if a condition actually references it,
                // TranslateCondition's own "no resolvable producing column" gap fires naturally.
            }

            var mapped = TransformEmitter.MapPipelineTypeToSsisType(inputColumn.CachedDataType);
            if (mapped is null) continue; // unmapped buffer type -- simply unavailable as a reference, not a gap on its own
            references[inputColumn.CachedName] = new ColumnReference($"row.{inputColumn.CachedName}", mapped.Value);
        }

        // split.Branches[^1] is always the default (no condition of its own, see
        // PackagePlanner.PlanConditionalSplit); every earlier entry is a case, already in
        // EvaluationOrder.
        var conditions = new List<string>();
        var functionsUsed = new HashSet<string>();
        for (var i = 0; i < split.Branches.Count - 1; i++)
        {
            var branch = split.Branches[i];
            if (branch.FriendlyExpression is null)
            {
                return new RouterEmitResult(new EmitResult([], [new GenerationGap(routerClassName,
                    $"case '{branch.OutputName}' has no FriendlyExpression to translate")]), new HashSet<string>());
            }

            ExprNode ast;
            try
            {
                ast = SsisExpression.Parse(branch.FriendlyExpression);
            }
            catch (SsisExpressionError ex)
            {
                return new RouterEmitResult(new EmitResult([], [new GenerationGap(routerClassName,
                    $"case '{branch.OutputName}': could not parse expression '{branch.FriendlyExpression}': {ex.Message}")]), new HashSet<string>());
            }

            var translated = ExpressionTranslator.TranslateCondition(ast, references);
            if (translated is NotTranslatable notTranslatable)
            {
                return new RouterEmitResult(new EmitResult([], [new GenerationGap(routerClassName,
                    $"case '{branch.OutputName}': {notTranslatable.Reason}")]), new HashSet<string>());
            }

            var conditionExpr = ((TranslatedOk)translated).CSharpExpression;
            TransformEmitter.CollectSsisFunctions(conditionExpr, functionsUsed);
            conditions.Add(conditionExpr);
        }

        var defaultIndex = split.Branches.Count - 1;
        var lines = new List<string>
        {
            $"using {rowTypeNamespace};",
            "using Etl.Core.Abstractions;",
        };
        if (functionsUsed.Count > 0) lines.Add($"using {ssisFnNamespace};");
        lines.Add("");
        lines.Add($"namespace {ns};");
        lines.Add("");
        lines.Add($"public sealed class {routerClassName} : IRowRouter<{rowTypeName}>");
        lines.Add("{");
        lines.Add($"    public int SelectBranch({rowTypeName} row, in RowContext ctx)");
        lines.Add("    {");
        for (var i = 0; i < conditions.Count; i++)
            lines.Add($"        if ({conditions[i]}) return {i};");
        lines.Add($"        return {defaultIndex}; // {split.Branches[defaultIndex].OutputName}");
        lines.Add("    }");
        lines.Add("}");

        return new RouterEmitResult(
            new EmitResult([new GeneratedFile($"Mapping/{routerClassName}.cs", Rendering.JoinLines(lines))], []),
            functionsUsed);
    }
}
