using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Named rules over an already-parsed <see cref="PackageSpec"/> (plan §5.7), each producing
/// zero or more <see cref="FindingSpec"/>s. Every rule here is answerable from the model
/// slices 1-3 already built -- no SQL parsing (ScriptDom, plan §5.4) and no full expression
/// tokenization (plan §5.5) yet, both later-slice work; where a rule would ideally use that
/// data (e.g. "unused variable"), it falls back to a documented best-effort substring search
/// over the expression/SQL text already captured, rather than waiting for slice 5.
///
/// The non-determinism rule (plan §5.7's own last bullet) is a thin wrapper around
/// <see cref="NonDeterminismAnalyzer"/> rather than a second implementation of that logic.
/// </summary>
public static class RulesEngine
{
    private static readonly HashSet<string> LoopTypes = ["Microsoft.ForEachLoop", "STOCK:FORLOOP", "STOCK:FOREACHLOOP"];

    public static List<FindingSpec> Evaluate(PackageSpec package)
    {
        var findings = new List<FindingSpec>();
        var allExecutables = PackageTree.AllExecutables(package).ToList();

        RewriteEffortRules(package, allExecutables, findings);
        SemanticRiskRules(package, allExecutables, findings);
        EnvironmentCouplingRules(package, findings);
        DeadOrSuspiciousRules(package, allExecutables, findings);
        NonDeterminismRules(package, allExecutables, findings);

        return findings;
    }

    /// <summary>Cross-package rules -- currently just duplicate-package detection, which is meaningless for a single package in isolation.</summary>
    public static List<FindingSpec> EvaluatePortfolio(List<PackageSpec> packages)
    {
        var findings = new List<FindingSpec>();

        foreach (var group in packages.GroupBy(p => p.Sha256).Where(g => g.Count() > 1))
        {
            var names = string.Join(", ", group.Select(p => p.ObjectName).OrderBy(n => n, StringComparer.Ordinal));
            foreach (var pkg in group)
            {
                findings.Add(new FindingSpec
                {
                    RuleId = "duplicate-package",
                    Category = "DeadOrSuspicious",
                    Severity = "Warning",
                    PackageName = pkg.ObjectName,
                    Location = "(package)",
                    Message = $"Byte-identical to {group.Count() - 1} other package(s) in this portfolio (same SHA-256): {names}. Likely a copy-paste template that was never diverged, or genuinely redundant.",
                });
            }
        }

        return findings;
    }

    private static void RewriteEffortRules(PackageSpec package, List<ExecutableSpec> allExecutables, List<FindingSpec> findings)
    {
        foreach (var ex in allExecutables)
        {
            if (ex.ExecutableType is "Microsoft.ScriptTask" or "STOCK:ScriptTask")
            {
                Add(findings, "script-task-present", "RewriteEffort", "Error", package.ObjectName, ex.RefId,
                    "Script Task -- arbitrary .NET/VBA code with no structural model here; must be read and manually ported, not mechanically translated.");

                if (ex.ScriptTask?.SourceStripped == true)
                {
                    Add(findings, "script-source-stripped", "RewriteEffort", "Error", package.ObjectName, ex.RefId,
                        "Script Task's source was stripped from this package -- only the precompiled binary cache remains ([" + string.Join(", ", ex.ScriptTask.BinaryItemNames) + "]). There is no source to read or port; recovering intent requires decompiling the binary or finding the original project elsewhere.");
                }
            }
            if (ex.ExecutableType == "Microsoft.ExecuteProcess")
            {
                Add(findings, "execute-process-task-present", "RewriteEffort", "Error", package.ObjectName, ex.RefId,
                    "Execute Process Task -- shells out to an external executable; the replacement platform needs equivalent process-execution capability, and the called program itself is outside this extraction's scope entirely.");
            }
            if (LoopTypes.Contains(ex.ExecutableType))
            {
                Add(findings, "loop-present", "RewriteEffort", "Warning", package.ObjectName, ex.RefId,
                    "ForEach/For Loop container -- iteration logic and its enumerator configuration need explicit handling in a rewrite, not just the loop body's own tasks.");
            }

            if (ex.DataFlowTask is null) continue;
            foreach (var comp in ex.DataFlowTask.Pipeline.Components)
            {
                var location = $"{ex.RefId}/{comp.Name}";
                if (comp.IsUnresolvedLegacyClsid)
                {
                    // Deliberately NOT third-party-component: a bare CLSID this table has no
                    // evidence for might well be a legitimate stock Microsoft component under an
                    // old spelling -- contactInfo is vendor-supplied free text this tool does not
                    // trust to identify it (same "don't guess" reasoning as the Lookup join key).
                    Add(findings, "unmapped-legacy-clsid", "RewriteEffort", "Warning", package.ObjectName, location,
                        $"Component identified only by CLSID {comp.ComponentClassId} -- unrecognized by this tool's legacy-ID table, so its real type is unknown (contactInfo says '{comp.ContactInfo}', but that string is not trusted to identify the component). Confirm the real type and extend LegacyComponentIds.cs once known.");
                }
                else if (!comp.ComponentClassId.StartsWith("Microsoft.", StringComparison.Ordinal))
                {
                    Add(findings, "third-party-component", "RewriteEffort", "Error", package.ObjectName, location,
                        $"Component class '{comp.ComponentClassId}' is not a built-in Microsoft component -- likely needs a vendor-specific or custom replacement; the generic model here captures its properties but not its licensed behavior.");
                }
                if (comp.ComponentClassId.Contains("SCD", StringComparison.OrdinalIgnoreCase) || comp.ComponentClassId.Contains("SlowlyChangingDimension", StringComparison.OrdinalIgnoreCase))
                {
                    // Phase 7 of the unsupported-component-types plan gave this component a real
                    // structured payload (ScdPayload), so this finding no longer has to say only
                    // "reproduce this by hand" -- it now names the business key and each attribute's
                    // own SCD type, which is exactly the information a reviewer sizing the rewrite
                    // needs. The finding itself stays (SCD is genuinely high-effort even when
                    // `ssisx generate` wires it), just with evidence attached. ScdColumnType lives in
                    // Ssis.Extract.Codegen, which this project deliberately does not reference, so
                    // the raw values are decoded locally here -- the mapping itself is measured, see
                    // ScdPayload's own doc comment.
                    var detail = "";
                    if (comp.Scd is { } scd)
                    {
                        static string Describe(int? raw) => raw switch
                        {
                            1 => "business key",
                            2 => "changing (Type 1)",
                            3 => "historical (Type 2)",
                            4 => "fixed",
                            null => "unclassified",
                            _ => $"ColumnType={raw}",
                        };
                        var columns = string.Join(", ", scd.Columns.Select(c => $"{c.ColumnName} ({Describe(c.ColumnTypeRaw)})"));
                        detail = $" Dimension query: {scd.SqlCommand ?? "(none)"}. Current-row filter: {scd.CurrentRowWhere ?? "(none)"}. Columns: {(columns.Length == 0 ? "(none)" : columns)}.";
                    }

                    Add(findings, "scd-component-present", "RewriteEffort", "Error", package.ObjectName, location,
                        "Slowly Changing Dimension component -- a wizard-generated subgraph of several transforms working together; painful to reproduce by hand and worth flattening to explicit SQL/MERGE logic during rewrite rather than porting as-is." + detail);
                }
                if (comp.ComponentClassId == "Microsoft.OLEDBCommand")
                {
                    Add(findings, "oledb-command-present", "RewriteEffort", "Warning", package.ObjectName, location,
                        "OLE DB Command -- executes one SQL statement per row (RBAR). Always a performance finding, and the per-row parameter binding needs care in a rewrite.");
                }
                if (comp.ScriptComponent is not null)
                {
                    Add(findings, "script-component-present", "RewriteEffort", "Error", package.ObjectName, location,
                        "Script Component -- arbitrary .NET/VBA code with no structural model here (beyond the generic input/output columns already captured); must be read and manually ported, not mechanically translated.");

                    if (comp.ScriptComponent.SourceStripped)
                    {
                        Add(findings, "script-source-stripped", "RewriteEffort", "Error", package.ObjectName, location,
                            "Script Component's source was stripped from this package -- only the precompiled binary cache remains. There is no source to read or port; recovering intent requires decompiling the binary or finding the original project elsewhere.");
                    }
                }

                var isSource = comp.Inputs.Count == 0;
                if (!isSource && comp.Outputs.Any(o => o.IsErrorOut != true && o.SynchronousInputId is null))
                {
                    Add(findings, "async-transform-present", "RewriteEffort", "Warning", package.ObjectName, location,
                        "Asynchronous transform (its output isn't wired to the same buffer as its input -- e.g. Sort/Aggregate/Union All) -- a blocking or semi-blocking memory consumer; a performance-relevant fact for both the original package and its replacement.");
                }
            }
        }
    }

    private static void SemanticRiskRules(PackageSpec package, List<ExecutableSpec> allExecutables, List<FindingSpec> findings)
    {
        if (package.Checkpoints.SaveCheckpoints == true)
        {
            Add(findings, "checkpoints-enabled", "SemanticRisk", "Warning", package.ObjectName, "(package)",
                "Checkpoints are enabled -- restart-from-failure semantics need an equivalent in whatever replaces this package, or a rerun will silently redo already-completed work (or skip it, depending on checkpoint file state).");
        }
        if (package.ExecutionSemantics.TransactionOption == "Required")
        {
            Add(findings, "transaction-required", "SemanticRisk", "Warning", package.ObjectName, "(package)",
                "Package-level TransactionOption=Required -- implicit DTC-coordinated transaction scope; a rewrite must reproduce the same atomicity boundary explicitly or a partial failure leaves data in a different state than today.");
        }
        if (package.ExecutionSemantics.ForceExecutionResult is not null)
        {
            Add(findings, "forced-execution-result", "SemanticRisk", "Warning", package.ObjectName, "(package)",
                $"Package-level ForceExecutionResult={package.ExecutionSemantics.ForceExecutionResult} -- the package's reported outcome is being overridden regardless of what actually happened, which will mask a real failure from any orchestrator/monitor.");
        }

        foreach (var ex in allExecutables)
        {
            if (ex.TransactionOption == "Required")
            {
                Add(findings, "transaction-required", "SemanticRisk", "Warning", package.ObjectName, ex.RefId,
                    "TransactionOption=Required on this executable -- same DTC-scope concern as the package-level rule, at task/container granularity.");
            }
            if (ex.MaximumErrorCount is > 1)
            {
                Add(findings, "max-error-count-tolerant", "SemanticRisk", "Warning", package.ObjectName, ex.RefId,
                    $"MaximumErrorCount={ex.MaximumErrorCount} -- this executable tolerates more than one failure before the package itself fails, which can silently mask partial data loss.");
            }
            if (ex.ForcedExecutionValue is not null)
            {
                Add(findings, "forced-execution-result", "SemanticRisk", "Warning", package.ObjectName, ex.RefId,
                    $"ForcedExecutionValue={ex.ForcedExecutionValue} on this executable -- its reported outcome is overridden regardless of what actually happened.");
            }

            if (ex.DataFlowTask is null) continue;
            foreach (var comp in ex.DataFlowTask.Pipeline.Components)
            {
                var location = $"{ex.RefId}/{comp.Name}";
                if (comp.ValidateExternalMetadata == false)
                {
                    Add(findings, "validate-external-metadata-false", "SemanticRisk", "Info", package.ObjectName, location,
                        "ValidateExternalMetadata=false -- this component's cached column metadata is not checked against the live source/destination schema at validation time, so a schema drift will surface as a runtime failure (or silent truncation) instead of an upfront validation error.");
                }

                var connectedStartIds = ex.DataFlowTask.Pipeline.Paths.Select(p => p.StartId).ToHashSet();
                foreach (var output in comp.Outputs.Where(o => o.IsErrorOut == true))
                {
                    if (!connectedStartIds.Contains(output.RefId))
                    {
                        Add(findings, "error-output-unrouted", "SemanticRisk", "Warning", package.ObjectName, location,
                            $"Error output '{output.Name}' has no downstream path -- any row redirected to it (ErrorRowDisposition=RedirectRow on some column) has nowhere to go.");
                    }
                }
            }
        }
    }

    private static void EnvironmentCouplingRules(PackageSpec package, List<FindingSpec> findings)
    {
        if (package.ProtectionLevelName is "EncryptSensitiveWithPassword" or "EncryptAllWithPassword")
        {
            Add(findings, "protection-level-password-based", "EnvironmentCoupling", "Warning", package.ObjectName, "(package)",
                $"ProtectionLevel={package.ProtectionLevelName} -- the package can only be opened/deployed with the same password used to save it; a CI/CD pipeline needs that password as a secret, and losing it makes the sensitive content unrecoverable.");
        }
        // EncryptSensitiveWithUserKey was previously excluded from this rule entirely -- wrongly:
        // it DPAPI-encrypts every sensitive property (a connection manager's password, most
        // commonly) to the AUTHOR'S OWN Windows account, which makes it unreadable/unrecoverable on
        // ANY other machine, including this one right now. Different risk shape from the
        // password-based modes above (no shared secret to manage -- there is no way in at all
        // without the original author's account), so it gets its own wording, not reused text.
        else if (package.ProtectionLevelName == "EncryptSensitiveWithUserKey")
        {
            Add(findings, "protection-level-user-key-based", "EnvironmentCoupling", "Warning", package.ObjectName, "(package)",
                "ProtectionLevel=EncryptSensitiveWithUserKey -- every sensitive property is DPAPI-encrypted to the ORIGINAL AUTHOR's own Windows account, unrecoverable on any other machine (including this one). See the 'encrypted-connection-manager-secret' finding(s) below for exactly which connection manager(s) are affected.");
        }

        foreach (var cm in package.ConnectionManagers.Where(cm => cm.EncryptedProperties.Count > 0))
        {
            var propertyList = string.Join(", ", cm.EncryptedProperties);
            Add(findings, "encrypted-connection-manager-secret", "EnvironmentCoupling", "Error", package.ObjectName, cm.ObjectName,
                $"Connection manager '{cm.ObjectName}' carries a DPAPI-encrypted sensitive value ({propertyList}), undecryptable outside the original author's Windows account. This extractor never reads the ciphertext or reports a value -- a human must supply the real credential out of band before any rewrite of this package's connection can authenticate. Silently missing this is the risk: a clean-looking extraction with no visible sign a credential ever existed.");
        }

        var configsEntry = package.Unmapped.FirstOrDefault(u => u.Location == "Package/Configurations");
        if (configsEntry is not null)
        {
            Add(findings, "legacy-package-configurations", "EnvironmentCoupling", "Warning", package.ObjectName, "(package)",
                "Legacy Package-Deployment-Model configurations present alongside (or instead of) project parameters -- an older, less structured environment-coupling mechanism; verify it isn't the package's real source of truth for connection/path values before assuming project parameters (plan §4.1/§4.2) cover everything.");
        }

        foreach (var cm in package.ConnectionManagers)
        {
            if (cm.WasRedacted)
            {
                Add(findings, "password-present-in-connection-string", "EnvironmentCoupling", "Info", package.ObjectName, cm.ObjectName,
                    "Connection string carries a Password=/Pwd= value (redacted in this extraction). If it isn't routed through SSISDB's sensitive-parameter auto-split (see this repo's own CLAUDE.md 'Sensitive credential' section for the recipe), it's a plaintext credential sitting in the package file.");
            }

            var hasConnectionStringExpression = cm.PropertyExpressions.Any(pe => pe.PropertyName == "ConnectionString");
            var hasLiteralTarget = !string.IsNullOrEmpty(cm.Parsed?.Server) || !string.IsNullOrEmpty(cm.Parsed?.FilePath);
            if (!hasConnectionStringExpression && hasLiteralTarget)
            {
                Add(findings, "hardcoded-connection-string", "EnvironmentCoupling", "Warning", package.ObjectName, cm.ObjectName,
                    "Connection string is a static literal, not driven by a project/package parameter or property expression -- moving this package between environments (dev/test/prod) means editing the package itself.");
            }
        }
    }

    private static void DeadOrSuspiciousRules(PackageSpec package, List<ExecutableSpec> allExecutables, List<FindingSpec> findings)
    {
        foreach (var ex in allExecutables)
        {
            if (ex.Disabled == true)
            {
                Add(findings, "disabled-task", "DeadOrSuspicious", "Info", package.ObjectName, ex.RefId,
                    "Disabled in the package -- dead weight if intentional, or a forgotten debugging toggle if not; either way it's not part of what actually runs today.");
            }
        }

        var referencedConnectionDtsIds = allExecutables
            .Where(e => e.ExecuteSqlTask?.ConnectionRefRaw is not null)
            .Select(e => e.ExecuteSqlTask!.ConnectionRefRaw!)
            .ToHashSet();
        var referencedConnectionRefIds = allExecutables
            .Where(e => e.DataFlowTask is not null)
            .SelectMany(e => e.DataFlowTask!.Pipeline.Components)
            .SelectMany(c => c.Connections)
            .Where(c => c.ConnectionManagerRefRaw is not null)
            .Select(c => c.ConnectionManagerRefRaw!)
            .ToHashSet();

        foreach (var cm in package.ConnectionManagers)
        {
            var used = (cm.DtsId is not null && referencedConnectionDtsIds.Contains(cm.DtsId))
                       || (cm.RefId is not null && referencedConnectionRefIds.Contains(cm.RefId));
            if (!used)
            {
                Add(findings, "unused-connection-manager", "DeadOrSuspicious", "Info", package.ObjectName, cm.ObjectName,
                    "Not referenced by any Execute SQL Task or Data Flow Task component found in this package -- may be used only via a property expression this rule doesn't parse for, or may genuinely be dead.");
            }
        }

        var allExpressionText = CollectAllExpressionText(package, allExecutables);
        foreach (var v in package.Variables)
        {
            var qualifiedName = $"{v.Namespace}::{v.ObjectName}";
            var referenced = allExpressionText.Any(t => t.Contains(qualifiedName, StringComparison.Ordinal));
            if (!referenced)
            {
                Add(findings, "unused-variable", "DeadOrSuspicious", "Info", package.ObjectName, v.OwningContainerRefId,
                    $"'{qualifiedName}' does not appear (by substring match) in any property expression, SQL statement, parameter binding, or pipeline expression this package has. Best-effort text search, not a real parser -- confirm before deleting.");
            }
        }

        foreach (var ex in allExecutables.Where(e => e.DataFlowTask is not null))
        {
            var connectedStartIds = ex.DataFlowTask!.Pipeline.Paths.Select(p => p.StartId).ToHashSet();
            var connectedEndIds = ex.DataFlowTask.Pipeline.Paths.Select(p => p.EndId).ToHashSet();
            foreach (var comp in ex.DataFlowTask.Pipeline.Components)
            {
                var hasInputWiring = comp.Inputs.Count == 0 || comp.Inputs.Any(i => connectedEndIds.Contains(i.RefId));
                var hasOutputWiring = comp.Outputs.Count(o => o.IsErrorOut != true) == 0 || comp.Outputs.Any(o => o.IsErrorOut != true && connectedStartIds.Contains(o.RefId));
                if (!hasInputWiring || !hasOutputWiring)
                {
                    Add(findings, "unconnected-pipeline-component", "DeadOrSuspicious", "Warning", package.ObjectName, $"{ex.RefId}/{comp.Name}",
                        "Declares inputs and/or outputs that no <path> in this Data Flow Task actually connects -- dead in the buffer graph regardless of what its properties say.");
                }
            }
        }
    }

    private static void NonDeterminismRules(PackageSpec package, List<ExecutableSpec> allExecutables, List<FindingSpec> findings)
    {
        foreach (var col in NonDeterminismAnalyzer.Analyze(package, allExecutables))
        {
            var target = col.TargetTable is null ? "(no destination reached)" : $"{col.TargetTable}.{col.TargetColumn}";
            Add(findings, "non-deterministic-column", "NonDeterminism", "Info", package.ObjectName, $"{col.DataFlowTaskPath}/{col.SourceComponentName}",
                $"'{col.SourceColumnName}' ({col.Reason}) lands at {target} -- exclude this column from any parallel-run row-hash comparison (plan §5.8); see nondeterministic.json.");
        }
    }

    /// <summary>Every free-text field this build slice has that could plausibly contain a <c>@[Namespace::Name]</c> variable reference -- a substitute for the real expression harvest/tokenizer (plan §5.5, slice 5), used only for the best-effort unused-variable rule above.</summary>
    private static List<string> CollectAllExpressionText(PackageSpec package, List<ExecutableSpec> allExecutables)
    {
        var texts = new List<string>();
        texts.AddRange(package.PropertyExpressions.Select(pe => pe.Expression));
        texts.AddRange(package.ConnectionManagers.SelectMany(cm => cm.PropertyExpressions).Select(pe => pe.Expression));
        texts.AddRange(package.PrecedenceConstraints.Where(c => c.Expression is not null).Select(c => c.Expression!));

        foreach (var ex in allExecutables)
        {
            texts.AddRange(ex.PropertyExpressions.Select(pe => pe.Expression));
            texts.AddRange(ex.PrecedenceConstraints.Where(c => c.Expression is not null).Select(c => c.Expression!));

            if (ex.ExecuteSqlTask is not null)
            {
                if (ex.ExecuteSqlTask.SqlStatementSource is not null) texts.Add(ex.ExecuteSqlTask.SqlStatementSource);
                texts.AddRange(ex.ExecuteSqlTask.ParameterBindings.Where(p => p.DtsVariableName is not null).Select(p => p.DtsVariableName!));
                texts.AddRange(ex.ExecuteSqlTask.ResultBindings.Where(r => r.DtsVariableName is not null).Select(r => r.DtsVariableName!));
            }

            if (ex.DataFlowTask is not null)
            {
                foreach (var comp in ex.DataFlowTask.Pipeline.Components)
                {
                    texts.AddRange(comp.Properties.Where(p => p.Value is not null).Select(p => p.Value!));
                    foreach (var output in comp.Outputs)
                    {
                        texts.AddRange(output.Columns.Where(c => c.Expression is not null).Select(c => c.Expression!));
                    }
                    if (comp.ScriptComponent is not null)
                    {
                        texts.AddRange(comp.ScriptComponent.ReadOnlyVariables);
                        texts.AddRange(comp.ScriptComponent.ReadWriteVariables);
                    }
                }
            }

            // A ForEach Loop's own variable mapping and a Script Task's declared
            // ReadOnlyVariables/ReadWriteVariables are both real usages of a variable even
            // though neither is an "expression" in the plan §5.5 sense -- added after the
            // synthetic ForEach+Script fixture (docs/report-schema.md) surfaced this rule
            // wrongly flagging the loop's own iteration variable as unused.
            if (ex.ForEachLoop is not null)
            {
                texts.AddRange(ex.ForEachLoop.VariableMappings.Select(m => m.VariableName));
            }
            if (ex.ScriptTask is not null)
            {
                texts.AddRange(ex.ScriptTask.ReadOnlyVariables);
                texts.AddRange(ex.ScriptTask.ReadWriteVariables);
            }
        }

        return texts;
    }

    private static void Add(List<FindingSpec> findings, string ruleId, string category, string severity, string packageName, string location, string message) =>
        findings.Add(new FindingSpec { RuleId = ruleId, Category = category, Severity = severity, PackageName = packageName, Location = location, Message = message });
}
