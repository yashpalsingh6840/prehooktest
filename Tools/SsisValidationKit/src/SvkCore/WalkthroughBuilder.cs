using System.Text;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Svk.Core;

/// <summary>
/// Renders one package's execution-order walkthrough: real SSIS order (ExecutionOrderBuilder),
/// parallel-execution call-outs, and -- per Data Flow Task -- a table of each component's
/// inputs/outputs/lineage source plus its matching `ssisx conformance` DataFlow rule ID and
/// current claim. Pure string building; the caller (SvkCli) owns all file I/O.
/// </summary>
public static class WalkthroughBuilder
{
    public static string BuildMarkdown(PackageSpec package, List<ExecutionStep> steps, List<ConformanceRuleSpec> dataFlowRules, IReadOnlyDictionary<string, WalkthroughClaim> claims)
    {
        var ruleByComponentRefId = dataFlowRules.ToDictionary(r => r.Location);
        var sb = new StringBuilder();
        sb.AppendLine($"# {package.ObjectName} -- execution-order walkthrough");
        sb.AppendLine();
        sb.AppendLine("Regenerated on every `svk walkthrough` run. Validation status lives in the sibling `claims/` file and survives regeneration -- never hand-edit this file directly.");

        var rootDiagram = ControlFlowDiagramBuilder.BuildMermaid(package.Executables, package.PrecedenceConstraints);
        if (rootDiagram is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Control flow -- package root");
            sb.AppendLine();
            sb.AppendLine("Who runs after whom, and under what condition. An unlabeled arrow runs unconditionally once its predecessor succeeds (SSIS's own schema default); every other label is the real constraint. A box with no arrows at all ran independently of every other box shown here.");
            sb.AppendLine();
            sb.AppendLine("```mermaid");
            sb.Append(rootDiagram);
            sb.AppendLine("```");
        }

        var reportedGroups = new HashSet<int>();
        foreach (var step in steps)
        {
            var headingLevel = Math.Min(2 + step.Depth, 5);
            var heading = new string('#', headingLevel);
            var ex = step.Executable;

            sb.AppendLine();
            sb.AppendLine($"{heading} {ex.ObjectName ?? ex.RefId} ({ex.ExecutableType})");

            if (step.ParallelGroupIndex is { } gi && step.ParallelGroupSize > 1 && reportedGroups.Add(gi))
            {
                sb.AppendLine();
                sb.AppendLine($"> **Parallel:** SSIS ran {step.ParallelGroupSize} executables concurrently at this point in the control flow (no ordering constraint between them). Confirm the rewrite's own sequencing here is acceptable, or flag it for redesign.");
            }

            if (ex.Disabled == true)
            {
                sb.AppendLine();
                sb.AppendLine("_Disabled in the source package._");
            }

            foreach (var insightLine in ExecutableInsight.Describe(ex))
            {
                sb.AppendLine();
                sb.AppendLine($"> {insightLine}");
            }

            var containerDiagram = ControlFlowDiagramBuilder.BuildMermaid(ex.Children, ex.PrecedenceConstraints);
            if (containerDiagram is not null)
            {
                sb.AppendLine();
                sb.AppendLine($"**Control flow inside `{ex.ObjectName ?? ex.RefId}`:**");
                sb.AppendLine();
                sb.AppendLine("```mermaid");
                sb.Append(containerDiagram);
                sb.AppendLine("```");
            }

            if (ex.DataFlowTask is { } dft)
            {
                var dataFlowDiagram = DataFlowDiagramBuilder.BuildMermaid(dft.Pipeline);
                if (dataFlowDiagram is not null)
                {
                    sb.AppendLine();
                    sb.AppendLine("**Data flow -- which component feeds which:**");
                    sb.AppendLine();
                    sb.AppendLine("```mermaid");
                    sb.Append(dataFlowDiagram);
                    sb.AppendLine("```");
                }

                // Same order (and the same per-component Seq number) the diagram above numbers
                // its nodes with -- PipelineFlowOrder is the one shared computation both read --
                // not dft.Pipeline.Components' own declaration order, which spec.json sorts
                // alphabetically by name, unrelated to which component actually runs before which.
                var flow = PipelineFlowOrder.Compute(dft.Pipeline);
                var compByRefId = dft.Pipeline.Components.ToDictionary(c => c.RefId);

                sb.AppendLine();
                sb.AppendLine("Two rows sharing the same Seq have no dependency on each other (e.g. parallel branches off a Conditional Split/Multicast) -- neither runs \"before\" the other. Detail is computed from the component's own configuration (query/table, join type, case expressions, ...), not its Description property -- SSIS leaves that at its own default boilerplate on every component checked so far.");
                sb.AppendLine();
                sb.AppendLine("| Seq | Component | Type | Detail | Inputs | Outputs | Lineage source | Rule | Status | Reviewer | Note |");
                sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
                foreach (var refId in flow.OrderedComponentRefIds)
                {
                    var comp = compByRefId[refId];
                    var detail = ComponentInsight.Describe(comp);
                    var inputs = Truncate(string.Join("; ", comp.Inputs.SelectMany(i => i.Columns).Select(c => $"{c.CachedName}:{c.CachedDataType}")));
                    var outputs = Truncate(string.Join("; ", comp.Outputs.Where(o => o.IsErrorOut != true).SelectMany(o => o.Columns).Select(c => $"{c.Name}:{c.DataType}")));
                    var lineage = Truncate(string.Join("; ", dft.Lineage.Edges.Where(e => e.ToComponentRefId == comp.RefId).Select(e => $"{e.FromComponentName}.{e.FromColumnName}").Distinct()));

                    ruleByComponentRefId.TryGetValue(comp.RefId, out var rule);
                    var ruleId = rule?.RuleId ?? "";
                    claims.TryGetValue(ruleId, out var claim);

                    sb.AppendLine($"| {flow.LevelByRefId[refId]} | {Md(comp.Name)} | {Md(comp.ComponentClassId)} | {Md(detail)} | {Md(inputs)} | {Md(outputs)} | {Md(lineage)} | {Md(ruleId)} | {claim?.Status.ToString() ?? "NotValidated"} | {Md(claim?.Reviewer)} | {Md(Truncate(claim?.Note))} |");
                }
            }
        }

        return sb.ToString();
    }

    private static string Md(string? s) => (s ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    private static string Truncate(string? s, int max = 80) => string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s[..max] + "…" : s);
}
