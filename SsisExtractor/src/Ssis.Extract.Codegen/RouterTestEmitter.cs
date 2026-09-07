using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;
using Ssis.Runtime.Expressions;

namespace Ssis.Extract.Codegen;

/// <summary>
/// "Router -- Conditional Split" taxonomy row (Docs/Generated-Tests-Plan.md). <c>SelectBranch</c>
/// is a pure function -- no fake, no PackageHarness needed at all, just a constructed row and a
/// fixed <c>RowContext</c>, mirroring <see cref="TransformTestEmitter"/>'s own "compute the
/// expected value, don't guess it" discipline: the expected branch index is computed by
/// INDEPENDENTLY evaluating each case's own FriendlyExpression via
/// <see cref="Ssis.Runtime.Expressions.SsisExpression"/> (the same oracle library gate 2/
/// <c>ssisx testgen</c> already uses) against the SAME representative values
/// <see cref="TransformTestEmitter.RepresentativeLiteral"/>/<c>RepresentativeOracleValue</c>
/// assign to the row -- never a hand-guessed index. This proves the generated router's own C#
/// translation agrees with the oracle's evaluation of the ORIGINAL SSIS expression, for at least
/// one real input, not merely that <c>SelectBranch</c> can be called.
///
/// <para><b>Pilot scope, stated plainly:</b> a case whose FriendlyExpression cannot be parsed, or
/// evaluates to NULL/a non-boolean against the representative row (three-valued logic genuinely
/// can produce this), or references a Conditional Split input column with no resolvable CLR type,
/// skips the whole test with a non-blocking gap -- the same per-flow "this pilot doesn't cover it,
/// add one by hand" convention <see cref="TransformTestEmitter"/> already uses, never a guess at
/// what SelectBranch "probably" returns. A Data-Conversion-produced input column (nullable,
/// computed inline rather than a real row property -- see <see cref="RouterEmitter"/>'s own doc
/// comment) is likewise out of this pilot's scope, for the identical reason
/// <see cref="TransformTestEmitter"/> already states for its own Derived-Column case.</para>
/// </summary>
public static class RouterTestEmitter
{
    public static EmitResult Emit(
        string testNamespace, string routerNamespace, string routerClassName,
        string rowTypeNamespace, string rowTypeName, ConditionalSplitPlan split,
        PipelineComponentSpec? derivedColumn = null, PipelineComponentSpec? dataConversion = null)
    {
        var splitComponent = split.Component;
        // RouterEmitter itself already reports a hard gap for anything other than exactly one
        // input -- nothing new to add here.
        if (splitComponent.Inputs.Count != 1) return new EmitResult([], []);

        // A column produced by the flow's own SHARED, upstream Derived Column/Data Conversion is
        // a real buffer column RouterEmitter itself is happy to reference as `row.{name}` (it
        // makes no distinction -- a condition simply never happens to reference one in any
        // fixture built so far), but it is NOT a real property on the generated row TYPE at all:
        // a Derived Column's own output exists only via `ctx.LoadedAtUtc`/an inline expression at
        // Map() time, never as a raw source column. Caught for real, not guessed: constructing a
        // representative row with every split input column unconditionally (the first version of
        // this emitter) failed CS0117 the moment a shared "LoadedAtUtc <- GETUTCDATE()" Derived
        // Column fed a Conditional Split whose OWN condition never even referenced it --
        // SyntheticConditionalSplit.dtsx's real shape. Excluded here rather than specially routed
        // through ctx (unlike TransformTestEmitter, which DOES special-case GETUTCDATE()) --
        // simpler, and correct: if a case's condition genuinely referenced an excluded column,
        // SsisExpression.Evaluate throws "unresolved reference", caught below as the same
        // graceful "this pilot doesn't cover it" gap every other unresolvable shape already uses.
        var excludedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in derivedColumn?.Outputs.Where(o => o.IsErrorOut != true) ?? [])
            foreach (var col in output.Columns.Where(c => c.Expression is not null))
                excludedNames.Add(col.Name);
        foreach (var col in dataConversion?.DataConvert?.Columns ?? [])
            excludedNames.Add(col.OutputColumnName);

        var rowLiterals = new Dictionary<string, string>(StringComparer.Ordinal);
        var rowLiteralOrder = new List<string>();
        var env = new Dictionary<string, SsisValue>();
        foreach (var inputColumn in splitComponent.Inputs[0].Columns)
        {
            if (excludedNames.Contains(inputColumn.CachedName)) continue;
            var mapped = TransformEmitter.MapPipelineTypeToSsisType(inputColumn.CachedDataType);
            if (mapped is null) continue; // unmapped buffer type -- simply unavailable, same as RouterEmitter's own resolution
            if (SsisPipelineTypeMap.Resolve(inputColumn.CachedDataType) is not { } clrType) continue;

            env[inputColumn.CachedName] = TransformTestEmitter.RepresentativeOracleValue(mapped.Value);
            rowLiterals[inputColumn.CachedName] = TransformTestEmitter.RepresentativeLiteral(clrType.ClrTypeName);
            rowLiteralOrder.Add(inputColumn.CachedName);
        }

        if (rowLiteralOrder.Count == 0) return new EmitResult([], []); // nothing resolvable to build a row from

        // split.Branches[^1] is always the default (see PackagePlanner.PlanConditionalSplit);
        // every earlier entry is a case, already in EvaluationOrder -- the identical walk
        // RouterEmitter's own SelectBranch if/else-if chain performs, just evaluated through the
        // oracle instead of translated to C#.
        var expectedIndex = split.Branches.Count - 1;
        for (var i = 0; i < split.Branches.Count - 1; i++)
        {
            var branch = split.Branches[i];
            if (branch.FriendlyExpression is null) return new EmitResult([], []); // RouterEmitter already reports this

            SsisValue evaluated;
            try
            {
                evaluated = SsisExpression.Evaluate(branch.FriendlyExpression, env);
            }
            catch (SsisExpressionError ex)
            {
                return new EmitResult([], [new GenerationGap(routerClassName,
                    $"starter test not generated -- case '{branch.OutputName}' could not be evaluated against a representative row: {ex.Message}. Add an assertion by hand.",
                    IsBlocking: false, Kind: GapKind.TestOracle, EvidenceRefId: splitComponent.RefId)]);
            }

            if (evaluated.IsNull || evaluated.Type != SsisType.Bool)
            {
                return new EmitResult([], [new GenerationGap(routerClassName,
                    $"starter test not generated -- case '{branch.OutputName}' evaluated to {(evaluated.IsNull ? "NULL" : "a non-boolean value")} against a representative row, which this pilot does not assert. Add an assertion by hand.",
                    IsBlocking: false, Kind: GapKind.TestOracle, EvidenceRefId: splitComponent.RefId)]);
            }

            if (evaluated.AsBool) { expectedIndex = i; break; }
        }

        var rowInitLines = rowLiteralOrder.Select(name => $"            {name} = {rowLiterals[name]},").ToList();

        var lines = new List<string>
        {
            "// <auto-generated>",
            $"// Generated by `ssisx generate` -- a STARTER unit test for {routerClassName}.SelectBranch,",
            "// asserting a representative row lands in the branch the SAME oracle library (gate 2/",
            "// ssisx testgen) independently evaluates its own case expression to. Regenerating this",
            "// file freely discards any hand edits -- move those into a second file instead.",
            "// </auto-generated>",
            "#nullable enable",
            "using Etl.Core.Abstractions;",
            $"using {rowTypeNamespace};",
            $"using {routerNamespace};",
            "using Xunit;",
            "",
            $"namespace {testNamespace};",
            "",
            $"public class {routerClassName}Tests",
            "{",
            "    [Fact]",
            "    public void SelectBranch_RoutesARepresentativeRow_ToTheExpectedBranch()",
            "    {",
            $"        var row = new {rowTypeName}",
            "        {",
        };
        lines.AddRange(rowInitLines);
        lines.Add("        };");
        lines.Add($"        var ctx = new RowContext(RowNumber: 1, LoadedAtUtc: new DateTime(2020, 6, 15, 12, 0, 0, DateTimeKind.Utc), SourceName: {ProgramEmitter.CSharpStringLiteral(routerClassName)});");
        lines.Add("");
        lines.Add($"        var branch = new {routerClassName}().SelectBranch(row, ctx);");
        lines.Add("");
        lines.Add($"        // Expected via the oracle: case index {expectedIndex} (\"{split.Branches[expectedIndex].OutputName}\").");
        lines.Add($"        Assert.Equal({expectedIndex}, branch);");
        lines.Add("    }");
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"{routerClassName}Tests.cs", Rendering.JoinLines(lines))], []);
    }
}
