using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Covers the correction at the heart of this round: a Lookup's join key IS persisted in the saved
/// .dtsx, on the joining input column's own <c>JoinToReferenceColumn</c> property, so a Lookup flow
/// generates with no human in the loop. The human-confirmed decision path remains only as the
/// fallback for a Lookup that genuinely declares none.
/// </summary>
public class LookupFlowTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Generate_WiresALookupFlow_FromTheJoinKeyPersistedInThePackage()
    {
        var package = LoadSyntheticFixture("SyntheticLookupSingle.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        // No blocking gap: nothing about this flow needed a human.
        Assert.DoesNotContain(result.Gaps, g => g.IsBlocking);

        var transform = result.Files.Single(f => f.RelativePath == "Mapping/SyntheticLookupSingleTargetTransform.cs").Content;
        // The reference key column (CountryName) is deliberately named DIFFERENTLY from the input
        // column (Country) in this fixture, so these assertions fail if the key is ever guessed by
        // name-matching instead of read from JoinToReferenceColumn.
        Assert.Contains("Dictionary<string, LKP_CountryCache.ReferenceRow> lookup", transform);
        Assert.Contains("CountryCode = lookup[row.Country].CountryCode,", transform);
        Assert.Contains("Region = lookup[row.Country].Region,", transform);

        var program = result.Files.Single(f => f.RelativePath == "Program.cs").Content;
        Assert.Contains("await LKP_CountryCache.LoadAsync<string>(", program);
        Assert.Contains("r => r.CountryName,", program);
        // Constructed with the preloaded cache, not resolved by type -- the cache is a local.
        Assert.Contains("AddScoped<IRowTransform<SyntheticLookupSingleTargetSqlRow, SyntheticLookupSingleTarget>>(sp => new SyntheticLookupSingleTargetTransform(lKP_CountryCache));", program);
    }

    [Fact]
    public void Generate_ReportsAGap_ForALookupThatDeclaresNoJoinKey()
    {
        // SyntheticLookupSplit.dtsx is the real reachable case: built through the object model
        // without ever mapping its Lookup's input columns, so it genuinely has no join key. Its
        // absence there is what this project previously mistook for "SSIS never persists one".
        var package = LoadSyntheticFixture("SyntheticLookupSplit.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var gap = Assert.Single(result.Gaps, g => g.Kind == GapKind.LookupJoinKey);
        Assert.Contains("declares no join key", gap.Reason);
        // The cache scaffold is still emitted -- everything derivable still gets generated.
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/LookupCache.cs");
        // ...but nothing is wired.
        Assert.DoesNotContain(result.Files, f => f.RelativePath == "Program.cs");
    }

    [Fact]
    public void Generate_UsesAConfirmedDecision_WhenThePackageDeclaresNoJoinKey()
    {
        var package = LoadSyntheticFixture("SyntheticLookupSplit.dtsx");
        var decisions = new GapDecisions(new Dictionary<string, GapDecisionSpec>
        {
            ["LOOKUP-JOIN-KEY:SyntheticLookupSplit:DFT_LookupSplitDemo"] = new()
            {
                InputColumn = "CustomerID",
                ReferenceColumn = "CustomerID",
                ConfirmedBy = "a-human",
            },
        });

        var result = PackageGenerator.Generate(package, namespacePrefix: null, decisions);

        var outcome = Assert.Single(decisions.Outcomes);
        Assert.Equal(GapDecisionStatus.Applied, outcome.Status);
        // The "no join key" gap is gone -- generation moved on to this flow's NEXT real
        // limitation (its Lookup routes both a match and a no-match output onward), rather than
        // repeating a reason that no longer applies.
        Assert.DoesNotContain(result.Gaps, g => g.Reason.Contains("declares no join key"));
        Assert.Contains(result.Gaps, g => g.Reason.Contains("routes 2 outputs onward"));
    }

    [Theory]
    // A proposal nobody signed off is not a decision, however confident it claims to be.
    [InlineData(null, "CustomerID", "CustomerID", GapDecisionStatus.Unconfirmed)]
    // Confirmed but missing a field the gap kind requires.
    [InlineData("a-human", null, "CustomerID", GapDecisionStatus.Incomplete)]
    [InlineData("a-human", "CustomerID", null, GapDecisionStatus.Incomplete)]
    public void ResolveLookupJoinKey_RefusesAndRecordsWhy(string? confirmedBy, string? inputColumn, string? referenceColumn, GapDecisionStatus expected)
    {
        var decisions = new GapDecisions(new Dictionary<string, GapDecisionSpec>
        {
            ["G"] = new() { ConfirmedBy = confirmedBy, InputColumn = inputColumn, ReferenceColumn = referenceColumn },
        });

        Assert.Null(decisions.ResolveLookupJoinKey("G", currentEvidenceSha256: null));
        Assert.Equal(expected, Assert.Single(decisions.Outcomes).Status);
    }

    [Fact]
    public void ResolveLookupJoinKey_RefusesAStaleDecision()
    {
        var decisions = new GapDecisions(new Dictionary<string, GapDecisionSpec>
        {
            ["G"] = new()
            {
                ConfirmedBy = "a-human",
                InputColumn = "A",
                ReferenceColumn = "B",
                EvidenceSha256 = new string('a', 64),
            },
        });

        Assert.Null(decisions.ResolveLookupJoinKey("G", currentEvidenceSha256: new string('b', 64)));
        Assert.Equal(GapDecisionStatus.Stale, Assert.Single(decisions.Outcomes).Status);
    }

    [Fact]
    public void Orphans_ReportADecisionNamingAGapThatNeverCameUp()
    {
        var decisions = new GapDecisions(new Dictionary<string, GapDecisionSpec>
        {
            ["GONE"] = new() { ConfirmedBy = "a-human", InputColumn = "A", ReferenceColumn = "B" },
        });

        var orphan = Assert.Single(decisions.Orphans());
        Assert.Equal("GONE", orphan.GapId);
        Assert.Equal(GapDecisionStatus.Orphaned, orphan.Status);
    }
}
