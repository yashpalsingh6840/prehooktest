using System.Text;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;
using Ssis.Runtime.Expressions;

namespace Ssis.Extract.Cli;

/// <summary>
/// <c>ssisx testgen</c> -- gate 2 of Migration-Validation-Plan.md §4: turn every Derived
/// Column expression the extractor harvests (plan §5.5) into a generated xUnit test file,
/// with boundary cases the golden data won't contain (over-length casts, NULL/empty operand
/// splits, a too-short SUBSTRING window, a case-folding probe) derived from facts already in
/// the spec -- a cast's declared length, a column's <c>TruncationRowDisposition</c>, which
/// functions the expression's own tokenization found.
///
/// <b>Expected values are computed, not guessed:</b> for every generated case, this command
/// evaluates the SAME expression through <c>Ssis.Runtime.Expressions</c> (Gate 2's own
/// semantics library, itself verified against the real SSIS evaluator -- see
/// docs/gate2-schema.md) with the case's input values, and bakes the actual result into the
/// generated assertion. That makes the output a pinned, executable statement of "this is what
/// SSIS does today, given these inputs" -- exactly what a rewrite needs to be measured
/// against, and precisely why a case that the library itself cannot evaluate (an unmapped
/// referenced-column type, a non-deterministic function) is skipped and reported, never
/// silently guessed.
/// </summary>
internal static class TestGenCommand
{
    public static int Run(string[] args)
    {
        string? input = null;
        string? outDir = null;
        var recursive = false;
        var packageNames = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            try
            {
                switch (args[i])
                {
                    case "--input": input = RequireValue(args, ref i, "--input"); break;
                    case "--out": outDir = RequireValue(args, ref i, "--out"); break;
                    case "--recursive": recursive = true; break;
                    case "--package":
                        var pkgArg = RequireValue(args, ref i, "--package");
                        packageNames.AddRange(pkgArg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                        break;
                    default:
                        Console.Error.WriteLine($"error: unknown testgen option '{args[i]}'");
                        return 2;
                }
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 2;
            }
        }

        if (input is null || outDir is null)
        {
            Console.Error.WriteLine("error: --input and --out are required. Run 'ssisx --help'.");
            return 2;
        }

        using var loaded = PackageLoader.Load(input, noRedact: false, recursive, packageNames);
        if (packageNames.Count > 0 && loaded.Packages.Count == 0)
        {
            Console.Error.WriteLine("error: --package matched no packages under this --input -- nothing to testgen.");
            return 2;
        }
        var testGenDir = Path.Combine(outDir, "testgen");
        Directory.CreateDirectory(testGenDir);

        var skipped = new List<(string Package, string Location, string Reason)>();
        var generated = new List<(string Package, string Location, int CaseCount)>();

        foreach (var package in loaded.Packages)
        {
            var harvested = ExpressionHarvester.Harvest(package)
                .Where(h => h.Kind == "DerivedColumn")
                .ToDictionary(h => (h.Location, h.TargetProperty ?? ""));

            var classes = new List<string>();

            foreach (var ex in PackageTree.AllExecutables(package))
            {
                if (ex.DataFlowTask is null) continue;
                var pipeline = ex.DataFlowTask.Pipeline;
                var lineage = LineageBuilder.Build(pipeline);
                var columnTypes = BuildColumnTypeLookup(pipeline);

                foreach (var comp in pipeline.Components)
                {
                    // Newly computed OUTPUT columns, plus in-place ("Replace <column>") rewrites,
                    // which live on a readWrite INPUT column and declare no output column -- see
                    // GenerateClass's own doc comment for why walking outputs alone left gate 2
                    // blank for them.
                    var expressionColumns =
                        comp.Outputs
                            .SelectMany(o => o.Columns)
                            .Where(c => c.Expression is not null)
                            .Select(c => (c.Name, c.RefId, c.Expression, c.FriendlyExpression))
                        .Concat(comp.Inputs
                            .SelectMany(i => i.Columns)
                            .Where(c => c.Expression is not null)
                            .Select(c => (Name: c.CachedName, c.RefId, c.Expression, c.FriendlyExpression)));

                    {
                        foreach (var col in expressionColumns)
                        {
                            var location = $"{ex.RefId}/{comp.Name}";
                            if (!harvested.TryGetValue((location, col.Name), out var harvest))
                            {
                                skipped.Add((package.ObjectName, $"{location}.{col.Name}", "no matching harvested expression (unexpected -- ExpressionHarvester and this walk should visit the same columns)"));
                                continue;
                            }

                            var genResult = GenerateClass(package.ObjectName, comp.Name, col, harvest, lineage, columnTypes);
                            if (genResult.Error is not null)
                            {
                                skipped.Add((package.ObjectName, $"{location}.{col.Name}", genResult.Error));
                                continue;
                            }

                            classes.Add(genResult.ClassSource!);
                            generated.Add((package.ObjectName, $"{comp.Name}.{col.Name}", genResult.CaseCount));
                        }
                    }
                }
            }

            if (classes.Count == 0) continue;

            var filePath = Path.Combine(testGenDir, $"{package.ObjectName}.ExpressionTests.cs");
            File.WriteAllText(filePath, RenderFile(package.ObjectName, classes));
        }

        WriteSummary(Path.Combine(testGenDir, "testgen-summary.md"), generated, skipped);

        Console.WriteLine($"testgen: {generated.Count} expression(s) covered, {skipped.Count} skipped -- see {Path.Combine(testGenDir, "testgen-summary.md")}");
        return 0;
    }

    // --- per-expression generation ---------------------------------------------------------

    private sealed record GenResult(string? ClassSource, int CaseCount, string? Error);

    /// <summary>
    /// <paramref name="col"/> is a tuple rather than a <c>PipelineOutputColumnSpec</c> so that an
    /// in-place ("Replace &lt;column&gt;") Derived Column can generate tests too: that shape
    /// persists its expression on the component's own readWrite INPUT column and declares no
    /// output column at all, so an outputs-only walk generated no boundary test for it -- gate 2
    /// was silently blank for exactly the transformations most likely to be mistranslated. These
    /// are the only three members this method ever needed.
    /// </summary>
    private static GenResult GenerateClass(
        string packageName, string componentName, (string Name, string RefId, string? Expression, string? FriendlyExpression) col,
        HarvestedExpressionSpec harvest,
        LineageSpec lineage, Dictionary<string, PipelineOutputColumnSpec> columnTypes)
    {
        if (harvest.Functions.Any(f => f is "GETUTCDATE" or "GETDATE"))
            return new GenResult(null, 0, "expression calls a non-deterministic function (GETUTCDATE/GETDATE) -- excluded, same as the non-determinism manifest (plan §5.8) excludes these columns from golden-dataset comparison");

        var expression = col.FriendlyExpression ?? col.Expression!;

        var referencedNames = lineage.Edges
            .Where(e => e.Kind == "ExpressionDerived" && e.ToColumnRefId == col.RefId)
            .Select(e => e.FromColumnName)
            .Distinct()
            .ToList();

        var refTypes = new Dictionary<string, SsisType>();
        foreach (var edge in lineage.Edges.Where(e => e.Kind == "ExpressionDerived" && e.ToColumnRefId == col.RefId))
        {
            if (refTypes.ContainsKey(edge.FromColumnName)) continue;
            if (!columnTypes.TryGetValue(edge.FromColumnRefId, out var producerCol))
                return new GenResult(null, 0, $"referenced column '{edge.FromColumnName}' has no resolvable producer in this pipeline");
            var mapped = MapPipelineType(producerCol.DataType);
            if (mapped is null)
                return new GenResult(null, 0, $"referenced column '{edge.FromColumnName}' has unmapped pipeline data type '{producerCol.DataType}' -- add it to TestGenCommand.MapPipelineType before this expression can be covered");
            refTypes[edge.FromColumnName] = mapped.Value;
        }
        // A referenced name with no edge at all (shouldn't happen for a well-formed
        // DerivedColumn expression, but defends against a lineage gap silently producing an
        // unbound identifier at evaluation time) is caught the same way: report and skip.
        foreach (var name in referencedNames.Where(n => !refTypes.ContainsKey(n)))
            return new GenResult(null, 0, $"referenced column '{name}' could not be resolved to a producing column via lineage");

        // The outer cast, if any, is where the plan's own worked example gets its boundary
        // case from: "the cast lengths are explicit -- DT_WSTR,101 -- so generate a case that
        // EXCEEDS 101 chars". Parsed from the expression itself (not re-derived from
        // col.DataType/col.Length) since the FriendlyExpression's own outer cast is exactly
        // what a human reads as "the contract" and is guaranteed to agree with what this
        // library will evaluate.
        try
        {
            SsisExpression.Parse(expression);
        }
        catch (SsisExpressionError ex)
        {
            // A parse failure must degrade this one column to a skip, never crash the whole
            // command -- the same isolation PackageLoader already gives a bad package. Before
            // this fix the call above was unguarded, so this was a latent bug: the trailing
            // comment already claimed "throws early (skip+report)" but nothing caught it.
            return new GenResult(null, 0, $"expression could not be parsed: {ex.Message}");
        }
        var outerCast = TryGetOuterCast(expression);

        var cases = new List<TestCase>
        {
            BuildCase("Normal", referencedNames, refTypes, name => NormalValue(refTypes[name]), expression),
        };

        foreach (var name in referencedNames)
        {
            cases.Add(BuildCase($"{name}IsNull", referencedNames, refTypes, n => n == name ? SsisValue.Null(refTypes[n]) : NormalValue(refTypes[n]), expression));
            if (SsisTypes.IsString(refTypes[name]))
                cases.Add(BuildCase($"{name}IsEmpty", referencedNames, refTypes, n => n == name ? SsisValue.OfString("") : NormalValue(refTypes[n]), expression));
        }

        if (outerCast is { Type: SsisType.WStr or SsisType.Str, Arg1: int maxLen })
        {
            // Blunt but generically-correct boundary: make every string-typed referenced
            // column overlong at once. If the expression doesn't actually let that reach the
            // cast unbounded (e.g. it SUBSTRINGs an input down first), evaluating below won't
            // error and the case is dropped rather than asserted wrong -- see BuildCase's
            // caller loop.
            var overlong = referencedNames.Where(n => SsisTypes.IsString(refTypes[n])).ToList();
            if (overlong.Count > 0)
            {
                var pad = new string('X', maxLen + 5);
                var caseResult = TryBuildCase("ExceedsTargetLength", referencedNames, refTypes,
                    n => overlong.Contains(n) ? SsisValue.OfString(pad) : NormalValue(refTypes[n]), expression,
                    requireOutcome: TestOutcome.Error);
                if (caseResult is not null) cases.Add(caseResult);
            }
        }

        if (harvest.Functions.Contains("SUBSTRING"))
        {
            var stringCols = referencedNames.Where(n => SsisTypes.IsString(refTypes[n])).ToList();
            foreach (var name in stringCols)
            {
                var caseResult = TryBuildCase($"{name}ShorterThanSubstringWindow", referencedNames, refTypes,
                    n => n == name ? SsisValue.OfString("A") : NormalValue(refTypes[n]), expression, requireOutcome: null);
                if (caseResult is not null) cases.Add(caseResult);
            }
        }

        if (harvest.Functions.Contains("UPPER") || harvest.Functions.Contains("LOWER"))
        {
            foreach (var name in referencedNames.Where(n => SsisTypes.IsString(refTypes[n])))
            {
                var caseResult = TryBuildCase($"{name}CaseFolding", referencedNames, refTypes,
                    n => n == name ? SsisValue.OfString("straße") : NormalValue(refTypes[n]), expression, requireOutcome: null);
                if (caseResult is not null) cases.Add(caseResult);
            }
        }

        var className = SanitizeIdentifier($"{componentName}_{col.Name}") + "Tests";
        return new GenResult(RenderClass(className, packageName, componentName, col.Name, expression, cases), cases.Count, null);
    }

    private enum TestOutcome { Value, Null, Error }

    private sealed record TestCase(string Name, Dictionary<string, SsisValue> Env, string Expression, TestOutcome Outcome, string? ExpectedValue);

    private static TestCase BuildCase(string name, List<string> referencedNames, Dictionary<string, SsisType> types, Func<string, SsisValue> valueFor, string expression) =>
        TryBuildCase(name, referencedNames, types, valueFor, expression, requireOutcome: null)
        ?? throw new InvalidOperationException($"case '{name}' unexpectedly could not be evaluated at generation time");

    /// <summary>
    /// Evaluates the case at GENERATION time (not guessed) to learn its real outcome.
    /// <paramref name="requireOutcome"/> lets a boundary case that's only meaningful when it
    /// produces a specific outcome (e.g. the overlong-string case is only informative if it
    /// actually errors) be silently dropped rather than asserted against a wrong expectation
    /// when the expression's own logic (e.g. an internal SUBSTRING) prevents that outcome.
    /// </summary>
    private static TestCase? TryBuildCase(string name, List<string> referencedNames, Dictionary<string, SsisType> types, Func<string, SsisValue> valueFor, string expression, TestOutcome? requireOutcome)
    {
        var env = referencedNames.ToDictionary(n => n, valueFor);
        try
        {
            var result = SsisExpression.Evaluate(expression, env);
            var outcome = result.IsNull ? TestOutcome.Null : TestOutcome.Value;
            if (requireOutcome is { } req && req != outcome) return null;
            return new TestCase(name, env, expression, outcome, result.IsNull ? null : result.ToDisplayString());
        }
        catch (SsisExpressionError)
        {
            if (requireOutcome is { } req && req != TestOutcome.Error) return null;
            return new TestCase(name, env, expression, TestOutcome.Error, null);
        }
    }

    private static SsisValue NormalValue(SsisType type) => type switch
    {
        SsisType.WStr or SsisType.Str => SsisValue.OfString("Sample"),
        SsisType.I2 or SsisType.I4 or SsisType.I8 => SsisValue.OfInt(7, type),
        SsisType.R4 => SsisValue.OfFloat(7.5f),
        SsisType.R8 => SsisValue.OfDouble(7.5),
        SsisType.Numeric => SsisValue.OfDecimal(7.5m),
        SsisType.Bool => SsisValue.OfBool(true),
        SsisType.DbTimeStamp or SsisType.DbDate => SsisValue.OfDate(new DateTime(2020, 6, 15, 12, 0, 0), type),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>
    /// Pipeline column <c>dataType</c> attribute values actually observed across this PoC's
    /// two packages plus every synthetic fixture (per CLAUDE.md's own measured surface) --
    /// not the full SSIS DTS type list. Extend before relying on a type not listed here; an
    /// unmapped type causes that expression's tests to be skipped and reported, not guessed.
    /// </summary>
    private static SsisType? MapPipelineType(string? dataType) => dataType?.ToLowerInvariant() switch
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
        foreach (var comp in pipeline.Components)
        {
            foreach (var output in comp.Outputs)
            {
                foreach (var col in output.Columns)
                {
                    map.TryAdd(col.RefId, col);
                }
            }
        }
        return map;
    }

    private static (SsisType Type, int? Arg1)? TryGetOuterCast(string expression)
    {
        try
        {
            return SsisExpression.Parse(expression) is Cast c ? (c.Type, c.Arg1) : null;
        }
        catch (SsisExpressionError)
        {
            return null;
        }
    }

    // --- rendering ---------------------------------------------------------------------------

    private static string RenderFile(string packageName, List<string> classes) => $$"""
        // <auto-generated>
        // Generated by `ssisx testgen` from {{packageName}}.dtsx -- DO NOT EDIT BY HAND.
        // Regenerate after any change to this package's Derived Column expressions. Every
        // expected value below was computed by evaluating the expression through
        // Ssis.Runtime.Expressions (itself verified against the real SSIS evaluator -- see
        // docs/gate2-schema.md), NOT hand-authored, so a changed value on regeneration means
        // either the expression changed or the semantics library's own understanding did --
        // both worth reviewing before accepting a diff here.
        // </auto-generated>
        using System.Collections.Generic;
        using Ssis.Runtime.Expressions;
        using Xunit;

        namespace Ssis.Generated.Tests.{{SanitizeIdentifier(packageName)}};

        {{string.Join("\n", classes)}}
        """;

    private static string RenderClass(string className, string packageName, string componentName, string columnName, string expression, List<TestCase> cases) => $$"""
        /// <summary>{{packageName}}.dtsx -- Derived Column "{{componentName}}", column "{{columnName}}".</summary>
        public class {{className}}
        {
            private const string Expression = {{Literal(expression)}};

        {{string.Join("\n\n", cases.Select(RenderCase))}}
        }
        """;

    private static string RenderCase(TestCase c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"    [Fact]");
        sb.AppendLine($"    public void {SanitizeIdentifier(c.Name)}()");
        sb.AppendLine("    {");
        sb.AppendLine("        var env = new Dictionary<string, SsisValue>");
        sb.AppendLine("        {");
        foreach (var (name, value) in c.Env)
        {
            sb.AppendLine($"            [{Literal(name)}] = {RenderValueLiteral(value)},");
        }
        sb.AppendLine("        };");
        sb.AppendLine();
        switch (c.Outcome)
        {
            case TestOutcome.Error:
                sb.AppendLine("        Assert.Throws<SsisExpressionError>(() => SsisExpression.Evaluate(Expression, env));");
                break;
            case TestOutcome.Null:
                sb.AppendLine("        var result = SsisExpression.Evaluate(Expression, env);");
                sb.AppendLine("        Assert.True(result.IsNull);");
                break;
            case TestOutcome.Value:
                sb.AppendLine("        var result = SsisExpression.Evaluate(Expression, env);");
                sb.AppendLine("        Assert.False(result.IsNull);");
                sb.AppendLine($"        Assert.Equal({Literal(c.ExpectedValue!)}, result.ToDisplayString());");
                break;
        }
        sb.Append("    }");
        return sb.ToString();
    }

    private static string RenderValueLiteral(SsisValue v)
    {
        if (v.IsNull) return $"SsisValue.Null(SsisType.{v.Type})";
        return v.Type switch
        {
            SsisType.WStr or SsisType.Str => $"SsisValue.OfString({Literal(v.AsString)}, SsisType.{v.Type})",
            SsisType.I2 or SsisType.I4 or SsisType.I8 => $"SsisValue.OfInt({v.AsInt64}, SsisType.{v.Type})",
            SsisType.R4 => $"SsisValue.OfFloat({v.AsDouble.ToString(System.Globalization.CultureInfo.InvariantCulture)}f)",
            SsisType.R8 => $"SsisValue.OfDouble({v.AsDouble.ToString(System.Globalization.CultureInfo.InvariantCulture)})",
            SsisType.Numeric => $"SsisValue.OfDecimal({v.AsDecimal.ToString(System.Globalization.CultureInfo.InvariantCulture)}m)",
            SsisType.Bool => $"SsisValue.OfBool({(v.AsBool ? "true" : "false")})",
            SsisType.DbTimeStamp or SsisType.DbDate => $"SsisValue.OfDate(System.DateTime.Parse({Literal(v.AsDate.ToString("O"))}), SsisType.{v.Type})",
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private static string Literal(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string SanitizeIdentifier(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    private static void WriteSummary(string path, List<(string Package, string Location, int CaseCount)> generated, List<(string Package, string Location, string Reason)> skipped)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ssisx testgen -- summary");
        sb.AppendLine();
        sb.AppendLine("Gate 2 of Migration-Validation-Plan.md §4: generated xUnit tests for Derived Column expressions, one file per package under this directory. See docs/gate2-schema.md.");
        sb.AppendLine();
        sb.AppendLine($"**{generated.Count} expression(s) covered, {skipped.Count} skipped.**");
        sb.AppendLine();
        sb.AppendLine("## Covered");
        sb.AppendLine();
        sb.AppendLine("| Package | Location | Cases generated |");
        sb.AppendLine("|---|---|---|");
        foreach (var (pkg, loc, count) in generated.OrderBy(g => g.Package, StringComparer.Ordinal).ThenBy(g => g.Location, StringComparer.Ordinal))
        {
            sb.AppendLine($"| {pkg} | {loc} | {count} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Skipped -- not silently dropped, each has a reason");
        sb.AppendLine();
        if (skipped.Count == 0)
        {
            sb.AppendLine("_None._");
        }
        else
        {
            sb.AppendLine("| Package | Location | Reason |");
            sb.AppendLine("|---|---|---|");
            foreach (var (pkg, loc, reason) in skipped.OrderBy(s => s.Package, StringComparer.Ordinal).ThenBy(s => s.Location, StringComparer.Ordinal))
            {
                sb.AppendLine($"| {pkg} | {loc} | {reason} |");
            }
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{flag} requires a value");
        i++;
        return args[i];
    }
}
