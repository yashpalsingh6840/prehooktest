using Ssis.Extract.Model.Analysis;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Covers <see cref="GapDecisions.ResolveEncryptedSecretAcknowledgment"/> -- the ACKNOWLEDGMENT-only
/// decision path for <see cref="GapKind.EncryptedConnectionManagerSecret"/>, genuinely different from
/// <see cref="GapDecisions.ResolveLookupJoinKey"/> (covered in <c>LookupFlowTests</c>): there is no
/// value to substitute here, only a record that a human is aware and has supplied the real secret
/// out of band. <c>InputColumn</c>/<c>ReferenceColumn</c> are irrelevant to this path and must never
/// gate it -- a decisions.json entry for this gap kind carries no secret material at all.
/// </summary>
public class GapDecisionsTests
{
    [Fact]
    public void ResolveEncryptedSecretAcknowledgment_AppliesAConfirmedEntry()
    {
        var decisions = new GapDecisions(new Dictionary<string, GapDecisionSpec>
        {
            ["G"] = new() { ConfirmedBy = "a-human", ConfirmedOn = "2026-09-02" },
        });

        var acknowledged = decisions.ResolveEncryptedSecretAcknowledgment("G", currentEvidenceSha256: null);

        Assert.True(acknowledged);
        var outcome = Assert.Single(decisions.Outcomes);
        Assert.Equal(GapDecisionStatus.Applied, outcome.Status);
        Assert.Contains("a-human", outcome.Detail);
    }

    [Fact]
    public void ResolveEncryptedSecretAcknowledgment_RefusesAnUnconfirmedEntry()
    {
        var decisions = new GapDecisions(new Dictionary<string, GapDecisionSpec>
        {
            ["G"] = new(),
        });

        var acknowledged = decisions.ResolveEncryptedSecretAcknowledgment("G", currentEvidenceSha256: null);

        Assert.False(acknowledged);
        Assert.Equal(GapDecisionStatus.Unconfirmed, Assert.Single(decisions.Outcomes).Status);
    }

    [Fact]
    public void ResolveEncryptedSecretAcknowledgment_RefusesAStaleEntry()
    {
        var decisions = new GapDecisions(new Dictionary<string, GapDecisionSpec>
        {
            ["G"] = new() { ConfirmedBy = "a-human", EvidenceSha256 = new string('a', 64) },
        });

        var acknowledged = decisions.ResolveEncryptedSecretAcknowledgment("G", currentEvidenceSha256: new string('b', 64));

        Assert.False(acknowledged);
        Assert.Equal(GapDecisionStatus.Stale, Assert.Single(decisions.Outcomes).Status);
    }

    [Fact]
    public void ResolveEncryptedSecretAcknowledgment_ReturnsFalse_WhenNoDecisionRecorded()
    {
        var decisions = GapDecisions.None;

        var acknowledged = decisions.ResolveEncryptedSecretAcknowledgment("G", currentEvidenceSha256: null);

        Assert.False(acknowledged);
        Assert.Empty(decisions.Outcomes);
    }

    [Fact]
    public void ResolveEncryptedSecretAcknowledgment_IgnoresInputColumnReferenceColumn_UnlikeLookup()
    {
        // A confirmed entry with no InputColumn/ReferenceColumn at all -- the Lookup path
        // (ResolveLookupJoinKey) would report this Incomplete; this path must not, since those
        // fields are meaningless for an acknowledgment-only decision.
        var decisions = new GapDecisions(new Dictionary<string, GapDecisionSpec>
        {
            ["G"] = new() { ConfirmedBy = "a-human" },
        });

        Assert.True(decisions.ResolveEncryptedSecretAcknowledgment("G", currentEvidenceSha256: null));
        Assert.Equal(GapDecisionStatus.Applied, Assert.Single(decisions.Outcomes).Status);
    }
}
