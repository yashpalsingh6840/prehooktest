using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Semantic diff between two versions of the same package (plan §6.2's <c>ssisx diff</c>).
/// Deliberately not a text diff of two spec.json files -- those already diff cleanly
/// (deterministic output, plan §2.3), but a text diff can't distinguish "gained a task"
/// from "the designer layout moved". This compares the typed model at the level a migration
/// actually cares about.
///
/// The intended use is source-vs-deployed drift detection: extract the repo's <c>.dtsx</c>,
/// extract a deployed <c>.ispac</c> (<see cref="IspacContents"/>), diff the two. On a client
/// engagement that answers "which packages can I not trust the source of" on day one --
/// which is why it's worth having even without SSISDB access (plan §11 decision 4).
/// </summary>
public static class PackageDiffer
{
    public static PackageDiffSpec Diff(PackageSpec left, PackageSpec right)
    {
        var differences = new List<PackageDiffEntry>();

        if (left.Sha256 == right.Sha256)
        {
            return new PackageDiffSpec
            {
                LeftPackageName = left.ObjectName,
                RightPackageName = right.ObjectName,
                LeftSha256 = left.Sha256,
                RightSha256 = right.Sha256,
                Identical = true,
            };
        }

        CompareProperty(differences, "PackageProperties", "ObjectName", left.ObjectName, right.ObjectName);
        CompareProperty(differences, "PackageProperties", "ProtectionLevelName", left.ProtectionLevelName, right.ProtectionLevelName);
        CompareProperty(differences, "PackageProperties", "PackageFormatVersion", left.PackageFormatVersion?.ToString(), right.PackageFormatVersion?.ToString());
        CompareProperty(differences, "PackageProperties", "VersionBuild", left.VersionBuild, right.VersionBuild);
        CompareProperty(differences, "PackageProperties", "VersionGuid", left.VersionGuid, right.VersionGuid);
        CompareProperty(differences, "PackageProperties", "ExecutionSemantics.TransactionOption", left.ExecutionSemantics.TransactionOption, right.ExecutionSemantics.TransactionOption);
        CompareProperty(differences, "PackageProperties", "ExecutionSemantics.MaximumErrorCount", left.ExecutionSemantics.MaximumErrorCount?.ToString(), right.ExecutionSemantics.MaximumErrorCount?.ToString());
        CompareProperty(differences, "PackageProperties", "Checkpoints.SaveCheckpoints", left.Checkpoints.SaveCheckpoints?.ToString(), right.Checkpoints.SaveCheckpoints?.ToString());

        CompareSets(differences, "Parameters",
            left.Parameters.ToDictionary(p => p.Name, p => $"{p.DataTypeName}={p.Value} (sensitive={p.Sensitive}, required={p.Required})"),
            right.Parameters.ToDictionary(p => p.Name, p => $"{p.DataTypeName}={p.Value} (sensitive={p.Sensitive}, required={p.Required})"));

        CompareSets(differences, "Variables",
            left.Variables.ToDictionary(v => $"{v.Namespace}::{v.ObjectName}", v => $"{v.DeclaredDataTypeName}={v.Value} (expression={v.Expression})"),
            right.Variables.ToDictionary(v => $"{v.Namespace}::{v.ObjectName}", v => $"{v.DeclaredDataTypeName}={v.Value} (expression={v.Expression})"));

        CompareSets(differences, "ConnectionManagers",
            left.ConnectionManagers.ToDictionary(c => c.ObjectName, DescribeConnectionManager),
            right.ConnectionManagers.ToDictionary(c => c.ObjectName, DescribeConnectionManager));

        var leftExecutables = PackageTree.AllExecutables(left).ToList();
        var rightExecutables = PackageTree.AllExecutables(right).ToList();

        CompareSets(differences, "Executables",
            leftExecutables.ToDictionary(e => e.RefId, DescribeExecutable),
            rightExecutables.ToDictionary(e => e.RefId, DescribeExecutable));

        CompareSets(differences, "PrecedenceConstraints",
            AllConstraints(left, leftExecutables),
            AllConstraints(right, rightExecutables));

        CompareSets(differences, "Pipeline",
            AllPipelineComponents(leftExecutables),
            AllPipelineComponents(rightExecutables));

        return new PackageDiffSpec
        {
            LeftPackageName = left.ObjectName,
            RightPackageName = right.ObjectName,
            LeftSha256 = left.Sha256,
            RightSha256 = right.Sha256,
            Identical = false,
            Differences = differences,
        };
    }

    private static string DescribeConnectionManager(ConnectionManagerSpec cm)
    {
        // The redacted connection string, never the unredacted one: a diff report is exactly
        // the kind of artifact that gets pasted into a ticket.
        var expressions = string.Join("; ", cm.PropertyExpressions.Select(p => $"{p.PropertyName}={p.Expression}"));
        return $"{cm.CreationName} connectionString={cm.ConnectionString} expressions=[{expressions}]";
    }

    private static string DescribeExecutable(ExecutableSpec ex)
    {
        var sql = ex.ExecuteSqlTask?.SqlStatementSource is { } s ? $" sql={Normalize(s)}" : "";
        return $"{ex.ExecutableType} disabled={ex.Disabled} maxErrors={ex.MaximumErrorCount} transaction={ex.TransactionOption}{sql}";
    }

    private static Dictionary<string, string> AllConstraints(PackageSpec package, List<ExecutableSpec> executables)
    {
        var all = package.PrecedenceConstraints.Concat(executables.SelectMany(e => e.PrecedenceConstraints));
        var result = new Dictionary<string, string>();
        foreach (var c in all)
        {
            result[c.RefId ?? $"{c.From} -> {c.To}"] = $"{c.From} -> {c.To} value={c.Value} evalOp={c.EvalOp} expression={c.Expression} logicalAnd={c.LogicalAnd}";
        }
        return result;
    }

    private static Dictionary<string, string> AllPipelineComponents(List<ExecutableSpec> executables)
    {
        var result = new Dictionary<string, string>();
        foreach (var ex in executables.Where(e => e.DataFlowTask is not null))
        {
            foreach (var comp in ex.DataFlowTask!.Pipeline.Components)
            {
                // Output-column expressions are included because a changed Derived Column
                // expression is exactly the kind of drift this is for -- same component,
                // same columns, different transformation.
                var expressions = string.Join("; ", comp.Outputs
                    .SelectMany(o => o.Columns)
                    .Where(c => c.Expression is not null)
                    .Select(c => $"{c.Name}={c.FriendlyExpression ?? c.Expression}"));
                var target = comp.OleDbDestination?.OpenRowset is { } t ? $" target={t}" : "";
                result[comp.RefId] = $"{comp.ComponentClassId}{target} expressions=[{expressions}]";
            }
        }
        return result;
    }

    private static void CompareProperty(List<PackageDiffEntry> sink, string area, string identity, string? left, string? right)
    {
        if (left == right) return;
        sink.Add(new PackageDiffEntry { Area = area, Change = "Changed", Identity = identity, LeftValue = left, RightValue = right });
    }

    private static void CompareSets(List<PackageDiffEntry> sink, string area, Dictionary<string, string> left, Dictionary<string, string> right)
    {
        foreach (var key in left.Keys.Except(right.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            sink.Add(new PackageDiffEntry { Area = area, Change = "Removed", Identity = key, LeftValue = left[key], RightValue = null });
        }
        foreach (var key in right.Keys.Except(left.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            sink.Add(new PackageDiffEntry { Area = area, Change = "Added", Identity = key, LeftValue = null, RightValue = right[key] });
        }
        foreach (var key in left.Keys.Intersect(right.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (left[key] == right[key]) continue;
            sink.Add(new PackageDiffEntry { Area = area, Change = "Changed", Identity = key, LeftValue = left[key], RightValue = right[key] });
        }
    }

    /// <summary>Collapses whitespace so a reformatted-but-identical SQL statement doesn't read as a semantic change -- the one place where "the text differs" genuinely isn't what this tool is meant to report.</summary>
    private static string Normalize(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
