using Ssis.Extract.Model.Analysis;

namespace Ssis.Extract.Codegen;

/// <summary>One decision's outcome, for the report: which gap, what happened to it, and why.</summary>
public sealed record GapDecisionOutcome(string GapId, GapDecisionStatus Status, string? Detail = null);

/// <summary>
/// The confirmed Tier-1 decisions available to one package's generation, plus the audit trail of
/// what was accepted and what was refused.
///
/// <b>Refusing is the important half.</b> A decision is applied only when it is confirmed by a
/// human, complete for its gap kind, and still matches the evidence it was made against.
/// Everything else is reported and NOT used -- an unconfirmed proposal silently taking effect
/// would turn "a human decided this" into "something suggested this", which is the entire
/// distinction that makes an AI-assisted fill trustworthy.
/// </summary>
public sealed class GapDecisions
{
    private readonly Dictionary<string, GapDecisionSpec> _byGapId;
    private readonly List<GapDecisionOutcome> _outcomes = [];

    public static GapDecisions None { get; } = new(new Dictionary<string, GapDecisionSpec>());

    public GapDecisions(IReadOnlyDictionary<string, GapDecisionSpec> byGapId) =>
        _byGapId = new Dictionary<string, GapDecisionSpec>(byGapId, StringComparer.Ordinal);

    /// <summary>Every decision outcome recorded during generation, in the order they were resolved.</summary>
    public IReadOnlyList<GapDecisionOutcome> Outcomes => _outcomes;

    /// <summary>
    /// Resolves the Lookup join key for <paramref name="gapId"/>, or null with a recorded reason.
    /// <paramref name="currentEvidenceSha256"/> is the hash of the evidence as it stands NOW --
    /// pass null when it is not available, which skips only the staleness check.
    /// </summary>
    public (string InputColumn, string ReferenceColumn)? ResolveLookupJoinKey(string gapId, string? currentEvidenceSha256)
    {
        if (!_byGapId.TryGetValue(gapId, out var decision)) return null;

        if (string.IsNullOrWhiteSpace(decision.ConfirmedBy))
        {
            Record(gapId, GapDecisionStatus.Unconfirmed, "no ConfirmedBy -- a proposal, not a decision");
            return null;
        }

        if (decision.EvidenceSha256 is { Length: > 0 } recorded
            && currentEvidenceSha256 is { Length: > 0 } current
            && !string.Equals(recorded, current, StringComparison.OrdinalIgnoreCase))
        {
            Record(gapId, GapDecisionStatus.Stale,
                $"decided against evidence {recorded[..8]}..., package now hashes {current[..8]}... -- re-review before reuse");
            return null;
        }

        if (string.IsNullOrWhiteSpace(decision.InputColumn) || string.IsNullOrWhiteSpace(decision.ReferenceColumn))
        {
            Record(gapId, GapDecisionStatus.Incomplete, "a Lookup join key needs both InputColumn and ReferenceColumn");
            return null;
        }

        Record(gapId, GapDecisionStatus.Applied, $"{decision.InputColumn} -> {decision.ReferenceColumn} (confirmed by {decision.ConfirmedBy})");
        return (decision.InputColumn, decision.ReferenceColumn);
    }

    /// <summary>
    /// Decisions naming a GapId that never came up during generation. Same meaning, and the same
    /// reason for reporting it, as <c>ConformanceChecker</c>'s orphaned claims: the package
    /// changed underneath work someone already did, and silently dropping it would hide that.
    /// </summary>
    public IEnumerable<GapDecisionOutcome> Orphans()
    {
        var touched = _outcomes.Select(o => o.GapId).ToHashSet(StringComparer.Ordinal);
        return _byGapId.Keys
            .Where(id => !touched.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new GapDecisionOutcome(id, GapDecisionStatus.Orphaned, "no gap with this id was reported for this package"));
    }

    private void Record(string gapId, GapDecisionStatus status, string detail) =>
        _outcomes.Add(new GapDecisionOutcome(gapId, status, detail));
}
