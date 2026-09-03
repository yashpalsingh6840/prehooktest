using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Turns an already-parsed <see cref="PackageSpec"/> into the list of obligations a
/// replacement must satisfy -- gate 1 of <c>Migration-Validation-Plan.md</c> §3, one rule
/// category per bullet of that section.
///
/// <b>This is the extractor's first consumer-shaped output.</b> Everything else it emits
/// describes the old package; this states what the new one owes. That difference drives two
/// design choices worth not undoing:
/// <list type="number">
/// <item>Rule ids are readable and derived only from meaning-bearing identifiers (refIds,
/// object/column names) -- never document order, GUIDs, or counts -- because an
/// implementation claim file is keyed by them and hand-maintained for the length of a
/// migration. See <see cref="ConformanceRuleSpec.RuleId"/>.</item>
/// <item>Nothing here is filtered out for being uninteresting. A disabled task still
/// produces a rule (with its disabled state as evidence), because "we deliberately didn't
/// carry that over" is a decision a human should record via a <c>NotApplicable</c> claim,
/// not one this tool should make silently on their behalf.</item>
/// </list>
///
/// Pure/offline, same as <see cref="ComplexityScorer"/> and <see cref="LineageBuilder"/> --
/// no XML, no I/O.
/// </summary>
public static class ConformanceRulesBuilder
{
    private static readonly HashSet<string> ScriptTaskTypes = ["Microsoft.ScriptTask", "STOCK:ScriptTask"];

    public static List<ConformanceRuleSpec> Build(PackageSpec package)
    {
        var rules = new List<ConformanceRuleSpec>();
        var allExecutables = PackageTree.AllExecutables(package).ToList();

        ControlFlowRules(package, allExecutables, rules);
        OrderingRules(package, allExecutables, rules);
        DataFlowRules(package, allExecutables, rules);
        SourceColumnRules(package, allExecutables, rules);
        TargetRules(package, allExecutables, rules);
        TransformationRules(package, rules);
        SqlStatementRules(package, rules);
        ScriptCodeRules(package, allExecutables, rules);

        // Ordinal sort so the emitted file is deterministic regardless of walk order --
        // same stability contract as every other output this tool writes, and doubly
        // important here because this file gets diffed against a claim file by a human.
        return rules.OrderBy(r => r.RuleId, StringComparer.Ordinal).ToList();
    }

    private static void ControlFlowRules(PackageSpec package, List<ExecutableSpec> allExecutables, List<ConformanceRuleSpec> rules)
    {
        foreach (var ex in allExecutables)
        {
            var evidence = $"ExecutableType={ex.ExecutableType}";
            if (ex.Disabled == true) evidence += "; Disabled=true";
            if (ex.MaximumErrorCount is > 1) evidence += $"; MaximumErrorCount={ex.MaximumErrorCount}";
            if (ex.TransactionOption is not null and not "Supported") evidence += $"; TransactionOption={ex.TransactionOption}";

            var requirement = ex.Disabled == true
                // Phrased as a decision to record rather than work to do: a disabled task is
                // the single most common "is this still needed?" question in a migration, and
                // the honest answer is nobody knows until someone checks.
                ? $"'{ex.ObjectName}' ({ex.ExecutableType}) is DISABLED in the source package -- confirm it is intentionally not carried over (claim NotApplicable with a note), or implement it."
                : $"Must implement the step '{ex.ObjectName}' ({ex.ExecutableType}).";

            rules.Add(new ConformanceRuleSpec
            {
                RuleId = $"CONTROL-FLOW:{ex.RefId}",
                Category = "ControlFlow",
                PackageName = package.ObjectName,
                Location = ex.RefId,
                Requirement = requirement,
                Evidence = evidence,
            });
        }
    }

    private static void OrderingRules(PackageSpec package, List<ExecutableSpec> allExecutables, List<ConformanceRuleSpec> rules)
    {
        var constraints = package.PrecedenceConstraints
            .Concat(allExecutables.SelectMany(e => e.PrecedenceConstraints));

        foreach (var c in constraints)
        {
            // Value/EvalOp are null when SSIS omitted them because they were the schema
            // default -- resolved here rather than reported as "unknown", since a rewrite
            // author reading "Success" needs no further lookup and "null" invites a wrong guess.
            var value = c.Value ?? "Success";
            var evalOp = c.EvalOp ?? "Constraint";
            var evidence = $"{c.From} -> {c.To}; Value={value}; EvalOp={evalOp}";
            if (c.Expression is not null) evidence += $"; Expression={c.Expression}";
            if (c.LogicalAnd == true) evidence += "; LogicalAnd=true";

            var requirement = value == "Success" && evalOp == "Constraint"
                ? $"Must run '{ShortName(c.To)}' only after '{ShortName(c.From)}' succeeds."
                : $"Must reproduce the ordering constraint '{ShortName(c.From)}' -> '{ShortName(c.To)}' ({value}, {evalOp}) -- including its failure path, not just the happy path.";

            rules.Add(new ConformanceRuleSpec
            {
                RuleId = $"ORDERING:{c.RefId ?? $"{c.From}->{c.To}"}",
                Category = "Ordering",
                PackageName = package.ObjectName,
                Location = c.RefId ?? $"{c.From}->{c.To}",
                Requirement = requirement,
                Evidence = evidence,
            });
        }
    }

    private static void DataFlowRules(PackageSpec package, List<ExecutableSpec> allExecutables, List<ConformanceRuleSpec> rules)
    {
        foreach (var ex in allExecutables.Where(e => e.DataFlowTask is not null))
        {
            foreach (var comp in ex.DataFlowTask!.Pipeline.Components)
            {
                var isAsync = comp.Inputs.Count > 0 && comp.Outputs.Any(o => o.IsErrorOut != true && o.SynchronousInputId is null);
                var evidence = $"componentClassID={comp.ComponentClassId}";
                if (isAsync) evidence += "; asynchronous (blocking/semi-blocking -- buffers rows in memory)";

                rules.Add(new ConformanceRuleSpec
                {
                    RuleId = $"DATA-FLOW:{comp.RefId}",
                    Category = "DataFlow",
                    PackageName = package.ObjectName,
                    Location = comp.RefId,
                    Requirement = $"Must implement the data-flow step '{comp.Name}' ({DescribeComponent(comp)}).",
                    Evidence = evidence,
                });
            }
        }
    }

    /// <summary>
    /// Migration-Validation-Plan §3: "every source column the package reads is consumed or
    /// explicitly marked as intentionally dropped." A source column is one produced by a
    /// component with no inputs; <c>LineageSpec.UnusedColumns</c> already knows which are
    /// never consumed, so that fact rides along as evidence instead of being recomputed.
    /// </summary>
    private static void SourceColumnRules(PackageSpec package, List<ExecutableSpec> allExecutables, List<ConformanceRuleSpec> rules)
    {
        foreach (var ex in allExecutables.Where(e => e.DataFlowTask is not null))
        {
            var lineage = ex.DataFlowTask!.Lineage;
            var unusedRefIds = lineage.UnusedColumns.Select(u => u.ColumnRefId).ToHashSet(StringComparer.Ordinal);

            foreach (var comp in ex.DataFlowTask.Pipeline.Components.Where(c => c.Inputs.Count == 0))
            {
                foreach (var output in comp.Outputs.Where(o => o.IsErrorOut != true))
                {
                    foreach (var col in output.Columns)
                    {
                        var unused = unusedRefIds.Contains(col.RefId);
                        var evidence = $"type={DescribeType(col.DataType, col.Length, col.Precision, col.Scale)}";
                        if (col.TruncationRowDisposition is not null) evidence += $"; truncationRowDisposition={col.TruncationRowDisposition}";
                        if (col.ErrorRowDisposition is not null) evidence += $"; errorRowDisposition={col.ErrorRowDisposition}";
                        if (unused) evidence += "; READ BUT NEVER CONSUMED in the source package";

                        var requirement = unused
                            ? $"Source column '{col.Name}' (from '{comp.Name}') is read but never used downstream -- confirm dropping it is intentional (claim NotApplicable with a note)."
                            : $"Must consume source column '{col.Name}' from '{comp.Name}', or explicitly record it as intentionally dropped.";

                        rules.Add(new ConformanceRuleSpec
                        {
                            RuleId = $"SOURCE-COLUMN:{col.RefId}",
                            Category = "SourceColumn",
                            PackageName = package.ObjectName,
                            Location = col.RefId,
                            Requirement = requirement,
                            Evidence = evidence,
                        });
                    }
                }
            }
        }
    }

    /// <summary>
    /// The target side, three bullets of §3 at once: per-column producing rules, the target
    /// schema contract, and load semantics. All three come off a destination component's own
    /// input + payload, so they're built in one walk rather than three.
    /// </summary>
    private static void TargetRules(PackageSpec package, List<ExecutableSpec> allExecutables, List<ConformanceRuleSpec> rules)
    {
        // Best-effort only (see PrimaryKeyCandidateSpec's own doc comment for why a .dtsx
        // can never carry an authoritative answer) -- folded into TargetSchema's evidence so
        // a reviewer sees the row-matching candidate right next to the column contract,
        // rather than needing to cross-reference primary-keys.json separately.
        var primaryKeyCandidates = PrimaryKeyInference.Infer(package, allExecutables)
            .ToDictionary(c => c.DestinationComponentRefId);

        foreach (var ex in allExecutables.Where(e => e.DataFlowTask is not null))
        {
            foreach (var comp in ex.DataFlowTask!.Pipeline.Components.Where(c => c.OleDbDestination is not null))
            {
                var dest = comp.OleDbDestination!;
                var target = dest.OpenRowset ?? dest.SqlCommand ?? comp.Name;
                var mainInput = comp.Inputs.FirstOrDefault();

                foreach (var mapping in dest.ColumnMappings)
                {
                    rules.Add(new ConformanceRuleSpec
                    {
                        RuleId = $"TARGET-COLUMN:{target}.{mapping.ExternalColumnName}",
                        Category = "TargetColumn",
                        PackageName = package.ObjectName,
                        Location = $"{comp.RefId}.{mapping.ExternalColumnName}",
                        Requirement = $"Must produce a value for target column {target}.{mapping.ExternalColumnName}.",
                        Evidence = $"fed by pipeline column '{mapping.ComponentColumnName}'; target type={mapping.ExternalDataType ?? "(unknown)"}",
                    });
                }

                if (mainInput is not null && mainInput.ExternalMetadataColumns.Count > 0)
                {
                    var contract = string.Join(", ", mainInput.ExternalMetadataColumns
                        .Select(c => $"{c.Name} {DescribeType(c.DataType, c.Length, c.Precision, c.Scale)}"));

                    var pkNote = primaryKeyCandidates.TryGetValue(comp.RefId, out var pk) && pk.Confidence != "Unknown"
                        ? $"; candidate primary key (best-effort, confirm before relying on it): {string.Join(", ", pk.Columns)}"
                        : "";

                    rules.Add(new ConformanceRuleSpec
                    {
                        RuleId = $"TARGET-SCHEMA:{target}",
                        Category = "TargetSchema",
                        PackageName = package.ObjectName,
                        Location = comp.RefId,
                        Requirement = $"Target {target} must match the schema contract the package was built against (column names, types, lengths).",
                        // Design-time contract, not live DDL -- the plan pairs this with a
                        // check against the real database, which needs a connection this
                        // offline tool deliberately doesn't take.
                        Evidence = $"design-time external metadata: {contract}{pkNote}",
                    });
                }

                rules.Add(new ConformanceRuleSpec
                {
                    RuleId = $"LOAD-SEMANTICS:{comp.RefId}",
                    Category = "LoadSemantics",
                    PackageName = package.ObjectName,
                    Location = comp.RefId,
                    Requirement = $"Must match how '{comp.Name}' writes to {target} -- insert semantics, batching, and error handling, not just the resulting rows.",
                    Evidence = DescribeLoadSemantics(dest, mainInput),
                });
            }
        }
    }

    private static void TransformationRules(PackageSpec package, List<ConformanceRuleSpec> rules)
    {
        foreach (var expr in ExpressionHarvester.Harvest(package))
        {
            // The friendly form is what a human reviews and what a gate-2 test case would be
            // written against; the raw #{lineageId} form is unreadable out of context.
            var text = expr.FriendlyExpression ?? expr.Expression;
            var evidence = text;
            if (expr.Functions.Count > 0) evidence += $"  [functions: {string.Join(", ", expr.Functions)}]";
            if (expr.Casts.Count > 0) evidence += $"  [casts: {string.Join(", ", expr.Casts)}]";

            var what = expr.TargetProperty is not null
                ? $"{expr.Kind} '{expr.TargetProperty}'"
                : expr.Kind;

            rules.Add(new ConformanceRuleSpec
            {
                RuleId = $"TRANSFORMATION:{expr.Location}{(expr.TargetProperty is null ? "" : $".{expr.TargetProperty}")}",
                Category = "Transformation",
                PackageName = package.ObjectName,
                Location = expr.Location,
                Requirement = $"Must implement {what} equivalently, including NULL and truncation behaviour (gate 2 proves the logic; this only asserts it exists).",
                Evidence = evidence,
            });
        }
    }

    private static void SqlStatementRules(PackageSpec package, List<ConformanceRuleSpec> rules)
    {
        foreach (var sql in SqlHarvester.Harvest(package))
        {
            rules.Add(new ConformanceRuleSpec
            {
                RuleId = $"SQL:{sql.Location}",
                Category = "SqlStatement",
                PackageName = package.ObjectName,
                Location = sql.Location,
                Requirement = "Must run an equivalent SQL statement (or achieve the same effect by other means).",
                Evidence = Collapse(sql.Sql),
            });
        }
    }

    /// <summary>
    /// The one category no tool can verify -- see <see cref="ConformanceRuleSpec.MachineVerifiable"/>.
    /// The extractor hands over the source (Script Task/Component source extraction); reading
    /// it is a person's job, so these rules exist to make that person's sign-off explicit
    /// rather than implied.
    /// </summary>
    /// <summary>
    /// The exact <c>ScriptCode</c> <see cref="ConformanceRuleSpec.RuleId"/> for the Script
    /// Task/Component at <paramref name="refId"/> -- the single source of truth for this format,
    /// so a second caller (<c>ApplyFillsCommand</c>, linking an applied Tier-2 fill to the
    /// conformance rule it answers) computes the exact same id this builder does, rather than
    /// duplicating the string shape and risking drift between the two.
    /// </summary>
    public static string ScriptCodeRuleId(string refId) => $"SCRIPT-CODE:{refId}";

    private static void ScriptCodeRules(PackageSpec package, List<ExecutableSpec> allExecutables, List<ConformanceRuleSpec> rules)
    {
        foreach (var ex in allExecutables.Where(e => ScriptTaskTypes.Contains(e.ExecutableType)))
        {
            var script = ex.ScriptTask;
            var evidence = $"language={script?.Language ?? "(unknown)"}";
            if (script is not null)
            {
                if (script.ReadOnlyVariables.Count > 0) evidence += $"; reads {string.Join(", ", script.ReadOnlyVariables)}";
                if (script.ReadWriteVariables.Count > 0) evidence += $"; writes {string.Join(", ", script.ReadWriteVariables)}";
                evidence += script.SourceStripped
                    ? "; SOURCE STRIPPED -- only a compiled binary remains"
                    : $"; {script.ProjectItems.Count} source file(s) extracted to scripts/";
            }

            rules.Add(new ConformanceRuleSpec
            {
                RuleId = ScriptCodeRuleId(ex.RefId),
                Category = "ScriptCode",
                PackageName = package.ObjectName,
                Location = ex.RefId,
                Requirement = $"A human must read Script Task '{ex.ObjectName}' and port its behaviour deliberately -- no tool here understands what it does.",
                Evidence = evidence,
                MachineVerifiable = false,
            });
        }

        foreach (var ex in allExecutables.Where(e => e.DataFlowTask is not null))
        {
            foreach (var comp in ex.DataFlowTask!.Pipeline.Components.Where(c => c.ScriptComponent is not null))
            {
                var script = comp.ScriptComponent!;
                var evidence = $"language={script.Language ?? "(unknown)"}";
                if (script.ReadOnlyVariables.Count > 0) evidence += $"; reads {string.Join(", ", script.ReadOnlyVariables)}";
                if (script.ReadWriteVariables.Count > 0) evidence += $"; writes {string.Join(", ", script.ReadWriteVariables)}";
                evidence += script.SourceStripped
                    ? "; SOURCE STRIPPED -- only a compiled binary remains"
                    : $"; {script.SourceCodeItems.Count} source item(s) extracted to scripts/";

                rules.Add(new ConformanceRuleSpec
                {
                    RuleId = ScriptCodeRuleId(comp.RefId),
                    Category = "ScriptCode",
                    PackageName = package.ObjectName,
                    Location = comp.RefId,
                    Requirement = $"A human must read Script Component '{comp.Name}' and port its behaviour deliberately -- no tool here understands what it does.",
                    Evidence = evidence,
                    MachineVerifiable = false,
                });
            }
        }
    }

    private static string DescribeComponent(PipelineComponentSpec comp)
    {
        if (comp.ScriptComponent is not null) return "Script Component";
        if (comp.Lookup is not null) return $"Lookup against {comp.Lookup.ConnectionName ?? "(unknown connection)"}";
        if (comp.ConditionalSplit is not null) return $"Conditional Split, {comp.ConditionalSplit.Cases.Count} case(s) + default";
        if (comp.OleDbSource is not null) return $"OLE DB Source from {comp.OleDbSource.OpenRowset ?? "a SQL command"}";
        if (comp.OleDbDestination is not null) return $"OLE DB Destination into {comp.OleDbDestination.OpenRowset ?? "a SQL command"}";
        if (comp.FlatFileSource is not null) return $"Flat File Source via {comp.FlatFileSource.ConnectionName ?? "(unknown connection)"}";
        return comp.ComponentClassId;
    }

    private static string DescribeLoadSemantics(OleDbDestinationPayload dest, PipelineInputSpec? mainInput)
    {
        var parts = new List<string>();
        if (dest.AccessMode is not null) parts.Add($"AccessMode={dest.AccessMode}");
        if (dest.FastLoadOptions is not null) parts.Add($"FastLoadOptions={dest.FastLoadOptions}");
        if (dest.FastLoadKeepIdentity is not null) parts.Add($"KeepIdentity={dest.FastLoadKeepIdentity}");
        if (dest.FastLoadKeepNulls is not null) parts.Add($"KeepNulls={dest.FastLoadKeepNulls}");
        if (dest.FastLoadMaxInsertCommitSize is not null) parts.Add($"MaxInsertCommitSize={dest.FastLoadMaxInsertCommitSize}");
        if (dest.CommandTimeout is not null) parts.Add($"CommandTimeout={dest.CommandTimeout}");
        if (mainInput?.ErrorRowDisposition is not null) parts.Add($"errorRowDisposition={mainInput.ErrorRowDisposition}");
        if (mainInput?.TruncationRowDisposition is not null) parts.Add($"truncationRowDisposition={mainInput.TruncationRowDisposition}");
        return parts.Count > 0 ? string.Join("; ", parts) : "(no load options declared)";
    }

    private static string DescribeType(string? dataType, int? length, int? precision, int? scale)
    {
        if (dataType is null) return "(unknown)";
        if (length is > 0) return $"{dataType}({length})";
        if (precision is > 0) return $"{dataType}({precision},{scale ?? 0})";
        return dataType;
    }

    /// <summary>A refId is a long backslash path; the last segment is the object's own name and is what a requirement sentence should read as.</summary>
    private static string ShortName(string refId)
    {
        var i = refId.LastIndexOf('\\');
        return i >= 0 && i < refId.Length - 1 ? refId[(i + 1)..] : refId;
    }

    /// <summary>SQL is multi-line; evidence is a one-line field in a CSV/table. The full text is already an addressable file under <c>sql/</c>, so collapsing here loses nothing.</summary>
    private static string Collapse(string s) =>
        string.Join(" ", s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
