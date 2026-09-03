using Ssis.Extract.Model.Analysis;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Joins generated <see cref="ConformanceRuleSpec"/>s against the rewrite author's
/// <see cref="ImplementationClaimSpec"/>s to produce the gate-1 result -- the plan's
/// "N rules in spec, M implemented, K unaccounted for" progress metric.
///
/// Pure/offline; the CLI owns all file I/O. Kept separate from
/// <see cref="ConformanceRulesBuilder"/> because the two halves have genuinely different
/// lifecycles: rules are regenerated from the .dtsx whenever the package is re-read, claims
/// accumulate by hand over a migration.
/// </summary>
public static class ConformanceChecker
{
    public const string StatusImplemented = "Implemented";
    public const string StatusNotApplicable = "NotApplicable";
    public const string StatusPending = "Pending";

    /// <summary>One rule joined to its claim (if any), plus why it is or isn't counted as accounted for.</summary>
    public sealed record CheckedRule(ConformanceRuleSpec Rule, ImplementationClaimSpec? Claim, string Outcome, string? Problem);

    public sealed record CheckResult(ConformanceReportSpec Report, List<CheckedRule> Rules, List<ImplementationClaimSpec> OrphanedClaims);

    public static CheckResult Check(string packageName, List<ConformanceRuleSpec> rules, List<ImplementationClaimSpec> claims)
    {
        // Last-one-wins on a duplicated RuleId would silently drop a claim; a hand-edited
        // file can easily contain one, so duplicates are reported as a problem instead.
        var claimsById = new Dictionary<string, ImplementationClaimSpec>(StringComparer.Ordinal);
        var duplicated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in claims)
        {
            if (!claimsById.TryAdd(claim.RuleId, claim)) duplicated.Add(claim.RuleId);
        }

        var checkedRules = new List<CheckedRule>();
        int implemented = 0, notApplicable = 0, pending = 0, unclaimed = 0, invalid = 0;

        foreach (var rule in rules)
        {
            claimsById.TryGetValue(rule.RuleId, out var claim);

            if (claim is null)
            {
                unclaimed++;
                checkedRules.Add(new CheckedRule(rule, null, "Unclaimed", "no claim for this rule -- was it added after the claim file was last reviewed?"));
                continue;
            }

            if (duplicated.Contains(rule.RuleId))
            {
                invalid++;
                checkedRules.Add(new CheckedRule(rule, claim, "Invalid", "duplicate claims for this rule id"));
                continue;
            }

            switch (claim.Status)
            {
                case StatusImplemented:
                    implemented++;
                    checkedRules.Add(new CheckedRule(rule, claim, StatusImplemented, null));
                    break;

                case StatusNotApplicable when string.IsNullOrWhiteSpace(claim.Note):
                    // An unexplained NotApplicable is precisely how a real requirement gets
                    // quietly deleted, so it fails rather than counting toward the total.
                    invalid++;
                    checkedRules.Add(new CheckedRule(rule, claim, "Invalid", "NotApplicable requires a Note explaining why this obligation is deliberately dropped"));
                    break;

                case StatusNotApplicable:
                    notApplicable++;
                    checkedRules.Add(new CheckedRule(rule, claim, StatusNotApplicable, null));
                    break;

                case StatusPending:
                    pending++;
                    checkedRules.Add(new CheckedRule(rule, claim, StatusPending, null));
                    break;

                default:
                    invalid++;
                    checkedRules.Add(new CheckedRule(rule, claim, "Invalid", $"unknown Status '{claim.Status}' -- expected {StatusImplemented}, {StatusNotApplicable}, or {StatusPending}"));
                    break;
            }
        }

        var ruleIds = rules.Select(r => r.RuleId).ToHashSet(StringComparer.Ordinal);
        var orphaned = claims
            .Where(c => !ruleIds.Contains(c.RuleId))
            .OrderBy(c => c.RuleId, StringComparer.Ordinal)
            .ToList();

        var report = new ConformanceReportSpec
        {
            PackageName = packageName,
            TotalRules = rules.Count,
            Implemented = implemented,
            NotApplicable = notApplicable,
            Pending = pending,
            Unclaimed = unclaimed,
            InvalidClaims = invalid,
            OrphanedClaims = orphaned.Count,
            AccountedForPercent = rules.Count == 0 ? 100 : Math.Round(100.0 * (implemented + notApplicable) / rules.Count, 2),
        };

        return new CheckResult(report, checkedRules, orphaned);
    }

    /// <summary>
    /// The gate: passes only when every rule is Implemented or explained-NotApplicable, with
    /// no invalid or orphaned claims. Deliberately strict -- Migration-Validation-Plan §7's
    /// "'it's down to 3 rows' is not a pass" applies to obligations too.
    /// </summary>
    public static bool Passes(ConformanceReportSpec report) =>
        report.Pending == 0 && report.Unclaimed == 0 && report.InvalidClaims == 0 && report.OrphanedClaims == 0;

    /// <summary>The stub written when no claim file exists yet: every rule Pending, so the file is a ready-made migration checklist for that package rather than something to author from scratch.</summary>
    public static List<ImplementationClaimSpec> StubClaims(List<ConformanceRuleSpec> rules) =>
        rules.Select(r => new ImplementationClaimSpec
        {
            RuleId = r.RuleId,
            Status = StatusPending,
            ImplementedBy = null,
            Note = null,
        }).ToList();
}
