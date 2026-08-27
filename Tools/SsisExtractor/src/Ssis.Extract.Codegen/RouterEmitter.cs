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
    public static EmitResult Emit(string ns, string routerClassName, string rowTypeNamespace, string rowTypeName, ConditionalSplitPlan split)
    {
        var splitComponent = split.Component;
        if (splitComponent.Inputs.Count != 1)
        {
            return new EmitResult([], [new GenerationGap(routerClassName,
                $"Conditional Split '{splitComponent.Name}' has {splitComponent.Inputs.Count} input(s), expected exactly 1")]);
        }

        var references = new Dictionary<string, (string CSharpExpression, SsisType Type)>();
        foreach (var inputColumn in splitComponent.Inputs[0].Columns)
        {
            var mapped = TransformEmitter.MapPipelineTypeToSsisType(inputColumn.CachedDataType);
            if (mapped is null) continue; // unmapped buffer type -- simply unavailable as a reference, not a gap on its own
            references[inputColumn.CachedName] = ($"row.{inputColumn.CachedName}", mapped.Value);
        }

        // split.Branches[^1] is always the default (no condition of its own, see
        // PackagePlanner.PlanConditionalSplit); every earlier entry is a case, already in
        // EvaluationOrder.
        var conditions = new List<string>();
        for (var i = 0; i < split.Branches.Count - 1; i++)
        {
            var branch = split.Branches[i];
            if (branch.FriendlyExpression is null)
            {
                return new EmitResult([], [new GenerationGap(routerClassName,
                    $"case '{branch.OutputName}' has no FriendlyExpression to translate")]);
            }

            ExprNode ast;
            try
            {
                ast = SsisExpression.Parse(branch.FriendlyExpression);
            }
            catch (SsisExpressionError ex)
            {
                return new EmitResult([], [new GenerationGap(routerClassName,
                    $"case '{branch.OutputName}': could not parse expression '{branch.FriendlyExpression}': {ex.Message}")]);
            }

            var translated = ExpressionTranslator.TranslateCondition(ast, references);
            if (translated is NotTranslatable notTranslatable)
            {
                return new EmitResult([], [new GenerationGap(routerClassName,
                    $"case '{branch.OutputName}': {notTranslatable.Reason}")]);
            }

            conditions.Add(((TranslatedOk)translated).CSharpExpression);
        }

        var defaultIndex = split.Branches.Count - 1;
        var lines = new List<string>
        {
            $"using {rowTypeNamespace};",
            "using Etl.Core.Abstractions;",
            "",
            $"namespace {ns};",
            "",
            $"public sealed class {routerClassName} : IRowRouter<{rowTypeName}>",
            "{",
            $"    public int SelectBranch({rowTypeName} row, in RowContext ctx)",
            "    {",
        };
        for (var i = 0; i < conditions.Count; i++)
            lines.Add($"        if ({conditions[i]}) return {i};");
        lines.Add($"        return {defaultIndex}; // {split.Branches[defaultIndex].OutputName}");
        lines.Add("    }");
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"Mapping/{routerClassName}.cs", Rendering.JoinLines(lines))], []);
    }
}
