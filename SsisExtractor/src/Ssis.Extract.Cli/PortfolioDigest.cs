using System.Text;
using Ssis.Extract.Codegen;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Cli;

/// <summary>
/// The one artifact designed to LEAVE a client environment when the full survey cannot.
///
/// <para>The rest of <c>ssisx report</c> is deliberately rich -- SQL text, Script Task source,
/// expressions, table and column names, file paths, server names. That is exactly what makes
/// it useful and exactly what makes it untransferable from a locked-down site. This digest is
/// the complement: every field is either a number, or a string drawn from a fixed vocabulary
/// that belongs to Microsoft (task types, component class ids, connection manager types,
/// expression function names, protection levels) or to this tool (rule ids, categories,
/// severities, effect kinds). Package names become stable pseudonyms (PKG-001...), which keep
/// the sections cross-referenceable without naming anything.</para>
///
/// <para><b>The safety property is an allow-list, not a redaction pass</b>, and that direction
/// matters. A redactor has to recognise every sensitive thing to remove it, so anything it has
/// never seen leaks by default. This builder can only emit values it went and asked for by
/// name, so anything unanticipated is absent by default. It is the same reasoning as
/// <c>ObservableEffectsBuilder</c>'s known-safe exclusion list, pointed the other way: there,
/// an unrecognised task must be flagged; here, an unrecognised string must not be printed.</para>
///
/// <para>The one place a non-vocabulary string can appear is a third-party pipeline component's
/// class id (e.g. a vendor SFTP task). Those are genuinely load-bearing for migration sizing --
/// they are usually the hardest things in a portfolio -- so they are emitted, but segregated
/// into their own section so a reviewer sees exactly which strings did not come from the known
/// vocabulary and can strike them if the vendor relationship is itself confidential.</para>
///
/// <para><see cref="ToConsoleDigest"/> is sized to be screenshotted or photographed off a
/// screen -- under 80 columns, ASCII only, no colour, aggregates only. That is often the only
/// export channel that actually exists on a client machine.</para>
/// </summary>
internal static class PortfolioDigest
{
    private const int Width = 78;

    /// <summary>
    /// A named record, not a <c>ValueTuple</c> -- <c>System.Text.Json</c> only serializes
    /// PROPERTIES by default, and a ValueTuple's Key/Count are public FIELDS, so every
    /// tuple-typed digest field used to serialize as a list of empty <c>{}</c> objects in
    /// <c>portfolio-digest.json</c>. Caught by actually reading the generated JSON, not
    /// assumed correct because the console/markdown renderers (which read the same values via
    /// direct string interpolation, never through JSON) looked fine. `portfolio-digest.json`
    /// is one of the two files this tool's whole design intends to leave a locked-down client
    /// site -- a silently empty export defeats that purpose.
    /// </summary>
    internal sealed record KeyCount(string Key, int Count);

    internal sealed record PackageDigestRow(
        string Pseudonym, string Classification, double Score, int Tasks, int Components,
        int Scripts, double Coverage, int Findings, int GenerationGaps);

    internal sealed record Digest(
        int PackageCount,
        int FailedInputCount,
        double MeanCoveragePercent,
        int MinCoveragePercent,
        List<KeyCount> Classifications,
        List<KeyCount> TaskTypes,
        List<KeyCount> ComponentTypes,
        List<KeyCount> ConnectionManagerTypes,
        List<KeyCount> ProtectionLevels,
        List<KeyCount> ExpressionFunctions,
        List<KeyCount> ExpressionCasts,
        List<KeyCount> EffectKinds,
        List<KeyCount> FindingsBySeverity,
        List<KeyCount> FindingsByRule,
        List<KeyCount> UnmappedReasons,
        List<string> NonMicrosoftComponentTypes,
        int PackagesWithScriptCode,
        int PackagesWithLoops,
        int PackagesWithEventHandlers,
        int TotalScriptTasks,
        int TotalScriptComponents,
        int TotalNonDeterministicColumns,
        int GeneratablePackageCount,
        int PackagesWithGenerationGaps,
        List<KeyCount> GenerationGapReasons,
        List<KeyCount> GenerationGapKinds,
        List<PackageDigestRow> Packages);

    /// <summary>
    /// <paramref name="perPackageGenerationGaps"/> is what actually answers "how much of this
    /// portfolio can `ssisx generate` produce today" -- the sizing number CLAUDE.md names as
    /// still unknown for a real client portfolio. Computed by calling
    /// <c>PackageGenerator.Generate</c> per package (pure, in-memory -- no files written here;
    /// `ssisx generate` itself is what a caller runs for the real output), so this digest
    /// reflects the generator's actual current gap-reporting, never a separate guess at it.
    /// </summary>
    public static Digest Build(
        List<PackageSpec> packages,
        List<PortfolioInventoryRow> inventory,
        List<FindingSpec> findings,
        List<HarvestedExpressionSpec> expressions,
        List<ObservableEffectSpec> effects,
        List<NonDeterministicColumnSpec> nonDeterministic,
        int failedInputCount,
        Dictionary<string, List<GenerationGap>> perPackageGenerationGaps)
    {
        // Pseudonyms are assigned over the ordinal-sorted package list, so the same folder
        // yields the same numbering on a re-run -- a reviewer can compare two digests.
        // Built with an explicit loop rather than ToDictionary: PackageLoader already drops
        // duplicate names, but a digest is not worth crashing a completed survey over.
        var pseudonyms = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < inventory.Count; i++)
        {
            pseudonyms[inventory[i].PackageName] = $"PKG-{i + 1:000}";
        }

        var allExecutables = packages.SelectMany(PackageTree.AllExecutables).ToList();
        var allComponents = allExecutables
            .Where(e => e.DataFlowTask?.Pipeline is not null)
            .SelectMany(e => e.DataFlowTask!.Pipeline!.Components)
            .ToList();

        var componentTypes = Tally(allComponents.Select(c => c.ComponentClassId));

        return new Digest(
            PackageCount: packages.Count,
            FailedInputCount: failedInputCount,
            MeanCoveragePercent: inventory.Count == 0 ? 0 : Math.Round(inventory.Average(r => r.CoveragePercent), 1),
            MinCoveragePercent: inventory.Count == 0 ? 0 : (int)Math.Floor(inventory.Min(r => r.CoveragePercent)),
            Classifications: Tally(inventory.Select(r => r.Complexity.Classification)),
            TaskTypes: Tally(allExecutables.Select(e => e.ExecutableType)),
            ComponentTypes: componentTypes,
            ConnectionManagerTypes: Tally(packages.SelectMany(p => p.ConnectionManagers).Select(c => c.CreationName)),
            ProtectionLevels: Tally(packages.Select(p => p.ProtectionLevelName)),
            ExpressionFunctions: Tally(expressions.SelectMany(e => e.Functions)),
            ExpressionCasts: Tally(expressions.SelectMany(e => e.Casts)),
            EffectKinds: Tally(effects.Select(e => e.Kind)),
            FindingsBySeverity: Tally(findings.Select(f => f.Severity)),
            FindingsByRule: Tally(findings.Select(f => f.RuleId)),
            UnmappedReasons: Tally(packages.SelectMany(p => p.Unmapped).Select(u => Shorten(u.Reason))),
            NonMicrosoftComponentTypes: componentTypes
                .Select(t => t.Key)
                .Where(k => !k.StartsWith("Microsoft.", StringComparison.Ordinal) && !k.StartsWith("DTS", StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList(),
            PackagesWithScriptCode: inventory.Count(r => r.Complexity.ScriptTaskCount + r.Complexity.ScriptComponentCount > 0),
            PackagesWithLoops: inventory.Count(r => r.Complexity.LoopCount > 0),
            PackagesWithEventHandlers: inventory.Count(r => r.Complexity.EventHandlerCount > 0),
            TotalScriptTasks: inventory.Sum(r => r.Complexity.ScriptTaskCount),
            TotalScriptComponents: inventory.Sum(r => r.Complexity.ScriptComponentCount),
            TotalNonDeterministicColumns: nonDeterministic.Count,
            GeneratablePackageCount: inventory.Count(r => GapCountFor(r.PackageName, perPackageGenerationGaps) == 0),
            PackagesWithGenerationGaps: inventory.Count(r => GapCountFor(r.PackageName, perPackageGenerationGaps) > 0),
            GenerationGapReasons: Tally(perPackageGenerationGaps.Values.SelectMany(gaps => gaps).Select(g => Shorten(g.Reason))),
            // Portfolio-wide, not per-package, same footing as GenerationGapReasons above -- this is
            // the "which component type recurs" signal chunk 4 exists to surface: a GapKind showing
            // up across several packages is the case FOR promoting it out of the AI-fill workflow
            // (Tier 1/2, one package at a time) and into the deterministic emitter (Tier 3, built
            // once with its own fixture/probe/test, and it pays off for every package that has it).
            // Deliberately excludes GapKind.Unclassified -- that bucket is either a plain advisory or
            // genuinely unclassified missing tool support, neither of which names a component TYPE.
            GenerationGapKinds: Tally(perPackageGenerationGaps.Values.SelectMany(gaps => gaps)
                .Where(g => g.Kind != GapKind.Unclassified).Select(g => g.Kind.ToString())),
            Packages: inventory.Select(r => new PackageDigestRow(
                Pseudonym: pseudonyms[r.PackageName],
                Classification: r.Complexity.Classification,
                Score: Math.Round(r.Complexity.Score, 1),
                Tasks: r.Complexity.TaskCount,
                Components: r.Complexity.PipelineComponentCount,
                Scripts: r.Complexity.ScriptTaskCount + r.Complexity.ScriptComponentCount,
                Coverage: Math.Round(r.CoveragePercent, 1),
                Findings: r.FindingsCount,
                GenerationGaps: GapCountFor(r.PackageName, perPackageGenerationGaps))).ToList());
    }

    /// <summary>
    /// BLOCKING gap count for a package -- defers to <see cref="GenerationGap.IsBlocking"/>,
    /// which `PackageGenerator` sets false for gaps that are informational rather than
    /// incomplete generation: the one every package with at least one flow gets
    /// unconditionally (`{Package}.Notification`, "a .dtsx carries no notification-recipient
    /// information" -- a .dtsx has no notion of "who to email", and the generated project
    /// still compiles and runs with empty recipient lists), and the SqlCommand-mode OLE DB
    /// Source column-name assumption in `BuildSqlFlowSource` (the flow IS fully generated;
    /// the gap just says "verify the column names before running"). Counting either as
    /// blocking would make "0 gaps" permanently unreachable for any real portfolio, which is
    /// worse than not measuring at all -- discovered by actually running this against the
    /// PoC's own 4 packages (Notification: even the two real, fully-supported ones showed 1
    /// "gap" apiece before that exclusion) and later against a real 5-package/~30-component
    /// client-shaped portfolio (`SSIS_From_Sandeep`, 2026-08-27: the SqlCommand advisory was
    /// still being counted as blocking via a Location-suffix check that only ever matched
    /// Notification -- fixed by moving the exclusion onto the gap itself).
    /// </summary>
    internal static bool IsBlockingGap(GenerationGap gap) => gap.IsBlocking;

    private static int GapCountFor(string packageName, Dictionary<string, List<GenerationGap>> perPackageGenerationGaps) =>
        perPackageGenerationGaps.TryGetValue(packageName, out var gaps) ? gaps.Count(IsBlockingGap) : 0;

    /// <summary>An unmapped Reason is tool-authored prose; only its first clause is kept, both to fit the digest and to avoid carrying along any element detail appended later in the sentence.</summary>
    private static string Shorten(string reason)
    {
        var cut = reason.IndexOf(" -- ", StringComparison.Ordinal);
        var head = cut > 0 ? reason[..cut] : reason;
        return head.Length > 60 ? head[..60] : head;
    }

    private static List<KeyCount> Tally(IEnumerable<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v))
              .GroupBy(v => v, StringComparer.Ordinal)
              .Select(g => new KeyCount(g.Key, g.Count()))
              .OrderByDescending(x => x.Count)
              .ThenBy(x => x.Key, StringComparer.Ordinal)
              .ToList();

    /// <summary>
    /// The screenshot/photograph form: ASCII, under 80 columns, aggregates only. Per-package
    /// rows are deliberately excluded here -- 50 of them do not fit on a screen, and the
    /// portfolio shape is what a sizing decision actually turns on. They are in the markdown.
    /// </summary>
    public static string ToConsoleDigest(Digest d)
    {
        var sb = new StringBuilder();
        Rule(sb, '=');
        Center(sb, "SSIS PORTFOLIO DIGEST");
        Center(sb, "aggregate counts only -- no names, SQL, or code");
        Rule(sb, '=');
        sb.AppendLine();
        sb.AppendLine($"  Packages read ............. {d.PackageCount}");
        sb.AppendLine($"  Packages unreadable ....... {d.FailedInputCount}");
        sb.AppendLine($"  Extraction coverage ....... {d.MeanCoveragePercent}% mean, {d.MinCoveragePercent}% worst");
        sb.AppendLine();
        sb.AppendLine($"  With script code .......... {d.PackagesWithScriptCode} package(s)  ({d.TotalScriptTasks} task(s), {d.TotalScriptComponents} component(s))");
        sb.AppendLine($"  With loop containers ...... {d.PackagesWithLoops} package(s)");
        sb.AppendLine($"  With event handlers ....... {d.PackagesWithEventHandlers} package(s)");
        sb.AppendLine($"  Non-deterministic columns . {d.TotalNonDeterministicColumns}");
        sb.AppendLine($"  Generatable (0 block. gaps) {d.GeneratablePackageCount} / {d.PackageCount} package(s), via `ssisx generate`");
        sb.AppendLine();

        Section(sb, "COMPLEXITY", d.Classifications, d.PackageCount);
        Section(sb, "TASK TYPES", d.TaskTypes, 0, 12);
        Section(sb, "DATA FLOW COMPONENT TYPES", d.ComponentTypes, 0, 12);
        Section(sb, "CONNECTION MANAGER TYPES", d.ConnectionManagerTypes, 0, 10);
        Section(sb, "OBSERVABLE EFFECT KINDS", d.EffectKinds, 0, 8);
        Section(sb, "EXPRESSION FUNCTIONS", d.ExpressionFunctions, 0, 12);
        Section(sb, "FINDINGS BY SEVERITY", d.FindingsBySeverity, 0, 5);
        Section(sb, "GENERATION GAP REASONS (ssisx generate)", d.GenerationGapReasons, 0, 10);
        Section(sb, "GENERATION GAP KINDS (recurrence = promote to the emitter)", d.GenerationGapKinds, 0, 8);

        if (d.NonMicrosoftComponentTypes.Count > 0)
        {
            sb.AppendLine("  NON-MICROSOFT COMPONENT TYPES (review before export)");
            foreach (var t in d.NonMicrosoftComponentTypes)
            {
                sb.AppendLine($"    ! {Trim(t, 68)}");
            }
            sb.AppendLine();
        }

        Rule(sb, '-');
        sb.AppendLine("  Everything above is a count or a Microsoft/tool vocabulary term.");
        sb.AppendLine("  Package names appear only as PKG-nnn. Nothing here is client data.");
        Rule(sb, '-');
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, List<KeyCount> rows, int total = 0, int max = 99)
    {
        if (rows.Count == 0) return;
        sb.AppendLine($"  {title}");
        foreach (var (key, count) in rows.Take(max))
        {
            var pct = total > 0 ? $"  ({count * 100 / Math.Max(total, 1)}%)" : "";
            sb.AppendLine($"    {Trim(key, 46).PadRight(46)} {count,5}{pct}");
        }
        if (rows.Count > max) sb.AppendLine($"    ... and {rows.Count - max} more (see the markdown digest)");
        sb.AppendLine();
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : string.Concat(s.AsSpan(0, n - 3), "...");
    private static void Rule(StringBuilder sb, char c) => sb.AppendLine(new string(c, Width));
    private static void Center(StringBuilder sb, string s) => sb.AppendLine(s.PadLeft((Width + s.Length) / 2));

    /// <summary>The same content as the console digest plus the per-package pseudonym table, for the case where a small text file IS allowed out.</summary>
    public static string ToMarkdown(Digest d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SSIS portfolio digest");
        sb.AppendLine();
        sb.AppendLine("Aggregate counts only. Every value below is a number, or a term from Microsoft's own");
        sb.AppendLine("SSIS vocabulary (task/component/connection types, expression functions, protection");
        sb.AppendLine("levels), or from this tool's own vocabulary (rule ids, severities, effect kinds).");
        sb.AppendLine("Package names appear only as stable pseudonyms. No SQL text, script source, table,");
        sb.AppendLine("column, file path, server, or database name is included by construction -- this is");
        sb.AppendLine("built from an allow-list of fields, not by redacting a fuller document.");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.Append(ToConsoleDigest(d));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Per package");
        sb.AppendLine();
        sb.AppendLine("| Package | Class | Score | Tasks | Components | Script units | Coverage % | Findings | Generation gaps |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var p in d.Packages)
        {
            sb.AppendLine($"| {p.Pseudonym} | {p.Classification} | {p.Score} | {p.Tasks} | {p.Components} | {p.Scripts} | {p.Coverage} | {p.Findings} | {(p.GenerationGaps == 0 ? "0 (ready)" : p.GenerationGaps.ToString())} |");
        }
        sb.AppendLine();
        AppendTable(sb, "Generation gap reasons (ssisx generate)", d.GenerationGapReasons);
        AppendTable(sb, "Generation gap kinds (recurrence = promote to the emitter)", d.GenerationGapKinds);
        AppendTable(sb, "Findings by rule", d.FindingsByRule);
        AppendTable(sb, "Expression casts", d.ExpressionCasts);
        AppendTable(sb, "Protection levels", d.ProtectionLevels);
        AppendTable(sb, "Unmapped element reasons", d.UnmappedReasons);
        return sb.ToString();
    }

    private static void AppendTable(StringBuilder sb, string title, List<KeyCount> rows)
    {
        if (rows.Count == 0) return;
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        sb.AppendLine("| Value | Count |");
        sb.AppendLine("|---|---:|");
        foreach (var (key, count) in rows) sb.AppendLine($"| `{key}` | {count} |");
        sb.AppendLine();
    }
}
