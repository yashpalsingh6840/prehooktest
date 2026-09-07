using Ssis.Extract.Model.Analysis;

namespace Ssis.Extract.Codegen.Tests;

public class GapIdentityTests
{
    [Theory]
    [InlineData(GapKind.LookupJoinKey, true, GapTier.MissingDatum)]
    [InlineData(GapKind.ScriptTask, true, GapTier.MissingLogic)]
    [InlineData(GapKind.ScriptComponentColumn, true, GapTier.MissingLogic)]
    // Both new phase-3 kinds reuse MissingDatum -- deliberately, per Docs/Generated-Tests-Plan.md's
    // own "Two new gap kinds -- no new tier" design: both need only IsBlocking=false plus a work
    // packet, which MissingDatum already grants with zero changes to GapTier itself. Reported
    // non-blocking regardless of the `isBlocking` argument here, since neither is ever constructed
    // with IsBlocking=true in practice -- confirmed by every real call site (RouterTestEmitter,
    // ScriptTaskEmitter, TransformEmitter, PackageGenerator).
    [InlineData(GapKind.TestOracle, false, GapTier.MissingDatum)]
    [InlineData(GapKind.LocalFileSourceData, false, GapTier.MissingDatum)]
    // An unclassified BLOCKING gap is missing tool support -- deliberately NOT AI-fillable, since
    // patching one per package would hide a systemic emitter gap behind N one-off patches.
    [InlineData(GapKind.Unclassified, true, GapTier.MissingToolSupport)]
    // A non-blocking gap is an advisory regardless: generation produced complete, wired output.
    [InlineData(GapKind.Unclassified, false, GapTier.Advisory)]
    public void TierOf_MapsEachKind(GapKind kind, bool isBlocking, GapTier expected)
    {
        var gap = new GenerationGap("Some.Location", "reason", isBlocking, kind);

        Assert.Equal(expected, GapIdentity.TierOf(gap));
    }

    [Fact]
    public void HasWorkPacket_IsTrueForTier1And2Only()
    {
        Assert.True(GapIdentity.HasWorkPacket(new GenerationGap("x", "r", true, GapKind.LookupJoinKey)));
        Assert.True(GapIdentity.HasWorkPacket(new GenerationGap("x", "r", true, GapKind.ScriptTask)));
        Assert.True(GapIdentity.HasWorkPacket(new GenerationGap("x", "r", true, GapKind.ScriptComponentColumn)));

        // Tier 3 and advisories get no packet -- see AiPacketEmitter's own doc comment.
        Assert.False(GapIdentity.HasWorkPacket(new GenerationGap("x", "r")));
        Assert.False(GapIdentity.HasWorkPacket(new GenerationGap("x", "r", false)));
    }

    [Fact]
    public void ComputeId_UsesTheReadableCategoryLocationShape()
    {
        var gap = new GenerationGap("StagingCustomers.FullName", "reason", true, GapKind.ScriptComponentColumn);

        Assert.Equal("SCRIPT-COLUMN:Package:StagingCustomers.FullName", GapIdentity.ComputeId("Package", gap));
    }

    [Fact]
    public void ComputeId_UsesTheNewPhase3Tokens_ForTestOracleAndLocalFileSourceData()
    {
        var oracleGap = new GenerationGap("CSPLIT_X", "reason", false, GapKind.TestOracle);
        var dataGap = new GenerationGap("FF_SRC_Foo", "reason", false, GapKind.LocalFileSourceData);

        Assert.Equal("TEST-ORACLE:Package:CSPLIT_X", GapIdentity.ComputeId("Package", oracleGap));
        Assert.Equal("LOCAL-DATA:Package:FF_SRC_Foo", GapIdentity.ComputeId("Package", dataGap));
    }

    /// <summary>A ScriptComponentColumn gap and its own companion TestOracle gap deliberately
    /// share a Location (see TransformEmitter's own doc comment) -- proving the different KIND
    /// token alone is enough to keep their GapIds distinct, with no collision/disambiguation
    /// hash needed.</summary>
    [Fact]
    public void AssignIds_KeepsACompanionTestOracleGap_DistinctFromItsSeamGap_AtTheSameLocation()
    {
        var gaps = new List<GenerationGap>
        {
            new("StagingCustomers.FullName", "seam", true, GapKind.ScriptComponentColumn),
            new("StagingCustomers.FullName", "companion test", false, GapKind.TestOracle),
        };

        var assigned = GapIdentity.AssignIds("Package", gaps);

        Assert.Equal(2, assigned.Count);
        Assert.Contains(assigned, a => a.GapId == "SCRIPT-COLUMN:Package:StagingCustomers.FullName");
        Assert.Contains(assigned, a => a.GapId == "TEST-ORACLE:Package:StagingCustomers.FullName");
    }

    /// <summary>
    /// The whole point of the id: a fill file is keyed by it and hand-maintained across a
    /// migration, so an id that moved when someone nudged the package in the designer would
    /// silently orphan the work behind it. Mirrors ConformanceTests' own
    /// RuleIds_AreStableAcrossRepeatedBuilds.
    /// </summary>
    [Fact]
    public void AssignIds_AreStableAcrossRepeatedBuilds()
    {
        var gaps = new List<GenerationGap>
        {
            new("StagingCustomers.FullName", "a", true, GapKind.ScriptComponentColumn),
            new("SCR_Validate", "b", true, GapKind.ScriptTask),
            new("DFT_Lookup", "c", true, GapKind.LookupJoinKey),
            new("Package.Notification", "d", false),
        };

        var first = GapIdentity.AssignIds("Package", gaps).Select(x => x.GapId).ToList();
        var second = GapIdentity.AssignIds("Package", gaps).Select(x => x.GapId).ToList();

        Assert.Equal(first, second);
        Assert.Equal(4, first.Distinct().Count());
    }

    [Fact]
    public void AssignIds_CollapsesAnExactDuplicate_ButDisambiguatesADifferentReasonAtTheSameLocation()
    {
        var duplicate = new GenerationGap("Entity.Col", "same reason", true, GapKind.ScriptComponentColumn);
        var gaps = new List<GenerationGap>
        {
            duplicate,
            new("Entity.Col", "same reason", true, GapKind.ScriptComponentColumn),
            new("Entity.Col", "a genuinely different reason", true, GapKind.ScriptComponentColumn),
        };

        var assigned = GapIdentity.AssignIds("Package", gaps);

        // Two work items, not three: an exact repeat is one gap reported twice.
        Assert.Equal(2, assigned.Count);
        Assert.Equal(2, assigned.Select(a => a.GapId).Distinct().Count());
        // Disambiguation is by a hash of the REASON, never list position -- so it survives an
        // emitter reporting its gaps in a different order.
        Assert.All(assigned, a => Assert.StartsWith("SCRIPT-COLUMN:Package:Entity.Col", a.GapId));
    }

    [Fact]
    public void ToFileName_StripsCharactersIllegalInAWindowsFileName()
    {
        var fileName = GapIdentity.ToFileName("SCRIPT-COLUMN:Package:Entity.Col~ab12cd34");

        Assert.DoesNotContain(':', fileName);
        Assert.DoesNotContain('~', fileName);
    }
}
