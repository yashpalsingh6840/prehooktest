using System.Text.RegularExpressions;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Collects every SSIS expression in a package into one flat table (plan §5.5) and
/// tokenizes each one for the functions/casts it uses. Two consumers, both named by the
/// plan: each row is a table-driven unit test case for the rewrite, and the tokenized
/// function set across a portfolio measures how much of the SSIS expression language a
/// replacement engine actually has to support.
/// </summary>
public static partial class ExpressionHarvester
{
    public static List<HarvestedExpressionSpec> Harvest(PackageSpec package)
    {
        var results = new List<HarvestedExpressionSpec>();

        foreach (var pe in package.PropertyExpressions)
        {
            results.Add(Build(package.ObjectName, "PropertyExpression", package.RefId, pe.PropertyName, pe.Expression, null));
        }

        foreach (var cm in package.ConnectionManagers)
        {
            foreach (var pe in cm.PropertyExpressions)
            {
                results.Add(Build(package.ObjectName, "ConnectionManagerExpression", cm.ObjectName, pe.PropertyName, pe.Expression, null));
            }
        }

        foreach (var v in package.Variables.Where(v => v.EvaluateAsExpression && v.Expression is not null))
        {
            results.Add(Build(package.ObjectName, "VariableExpression", v.OwningContainerRefId, $"{v.Namespace}::{v.ObjectName}", v.Expression!, null));
        }

        foreach (var c in package.PrecedenceConstraints.Where(c => c.Expression is not null))
        {
            results.Add(Build(package.ObjectName, "PrecedenceConstraint", c.RefId ?? $"{c.From} -> {c.To}", null, c.Expression!, null));
        }

        foreach (var ex in PackageTree.AllExecutables(package))
        {
            foreach (var pe in ex.PropertyExpressions)
            {
                results.Add(Build(package.ObjectName, "PropertyExpression", ex.RefId, pe.PropertyName, pe.Expression, null));
            }

            foreach (var v in ex.Variables.Where(v => v.EvaluateAsExpression && v.Expression is not null))
            {
                results.Add(Build(package.ObjectName, "VariableExpression", ex.RefId, $"{v.Namespace}::{v.ObjectName}", v.Expression!, null));
            }

            foreach (var c in ex.PrecedenceConstraints.Where(c => c.Expression is not null))
            {
                results.Add(Build(package.ObjectName, "PrecedenceConstraint", c.RefId ?? $"{c.From} -> {c.To}", null, c.Expression!, null));
            }

            if (ex.DataFlowTask is null) continue;
            foreach (var comp in ex.DataFlowTask.Pipeline.Components)
            {
                foreach (var output in comp.Outputs)
                {
                    foreach (var col in output.Columns.Where(c => c.Expression is not null))
                    {
                        results.Add(Build(package.ObjectName, "DerivedColumn", $"{ex.RefId}/{comp.Name}", col.Name, col.Expression!, col.FriendlyExpression));
                    }
                }

                // An in-place ("Replace <column>") Derived Column persists its expression on the
                // readWrite INPUT column and declares no output column, so walking outputs alone
                // missed the transformation entirely -- and everything downstream of this harvest
                // inherited the omission: the conformance obligations (gate 1), the generated
                // boundary tests (gate 2), expressions.csv, and the non-determinism manifest that
                // feeds gate 3's ExcludedColumns.
                foreach (var input in comp.Inputs)
                {
                    foreach (var col in input.Columns.Where(c => c.Expression is not null))
                    {
                        results.Add(Build(package.ObjectName, "DerivedColumn", $"{ex.RefId}/{comp.Name}", col.CachedName, col.Expression!, col.FriendlyExpression));
                    }
                }
            }
        }

        return results;
    }

    private static HarvestedExpressionSpec Build(string packageName, string kind, string location, string? targetProperty, string expression, string? friendlyExpression) => new()
    {
        PackageName = packageName,
        Kind = kind,
        Location = location,
        TargetProperty = targetProperty,
        Expression = expression,
        FriendlyExpression = friendlyExpression,
        ReferencedVariables = DistinctMatches(VariableRefPattern(), expression),
        ReferencedColumns = DistinctMatches(LineageRefPattern(), expression),
        // Tokenize the friendly form when there is one: a Derived Column's raw expression
        // writes function names in SSIS's own [BRACKET] form and stuffs whole lineageId
        // paths inline, both of which make plain NAME( scanning noisier than it needs to be.
        // Both forms are handled either way (see FunctionPattern) -- this just prefers the
        // cleaner input when it's available.
        Functions = ExtractFunctions(friendlyExpression ?? expression),
        Casts = DistinctMatches(CastPattern(), friendlyExpression ?? expression).Select(c => c.ToUpperInvariant()).Distinct().ToList(),
    };

    private static List<string> ExtractFunctions(string expression)
    {
        // Cast operators look like a function call to a naive NAME( scan -- (DT_WSTR,50) is
        // not a function, and counting it as one would inflate the "which functions must the
        // replacement support" number this exists to answer. They get their own list.
        return FunctionPattern().Matches(expression)
            .Select(m => (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToUpperInvariant())
            .Where(name => !name.StartsWith("DT_", StringComparison.Ordinal))
            .Distinct()
            .ToList();
    }

    private static List<string> DistinctMatches(Regex pattern, string input) =>
        pattern.Matches(input).Select(m => m.Groups[1].Value).Distinct().ToList();

    /// <summary>An SSIS variable/parameter reference: <c>@[Namespace::Name]</c>.</summary>
    [GeneratedRegex(@"@\[([^\]]+)\]")]
    private static partial Regex VariableRefPattern();

    /// <summary>A pipeline column reference inside a Derived Column's raw expression: <c>#{lineageId}</c>.</summary>
    [GeneratedRegex(@"#\{([^}]+)\}")]
    private static partial Regex LineageRefPattern();

    /// <summary>Either SSIS's bracketed function form (<c>[UPPER](</c>, how a Derived Column's raw expression stores them) or a plain <c>NAME(</c> call.</summary>
    [GeneratedRegex(@"\[([A-Za-z_][A-Za-z0-9_]*)\]\s*\(|\b([A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex FunctionPattern();

    /// <summary>An SSIS cast operator: <c>(DT_WSTR,101)</c>, <c>(DT_STR,50,1252)</c>, <c>(DT_I4)</c>.</summary>
    [GeneratedRegex(@"\(\s*(DT_[A-Z0-9_]+)\s*[,)]", RegexOptions.IgnoreCase)]
    private static partial Regex CastPattern();
}
