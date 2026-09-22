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

        var classFile = result.Files.Single(f => f.RelativePath == "SyntheticLookupSingle.cs").Content;
        Assert.Contains("private Dictionary<string, LKP_CountryCache.ReferenceRow> lKP_CountryCache = null!;", classFile);
        Assert.Contains("lKP_CountryCache = await LKP_CountryCache.LoadAsync<string>(SqlConnectionStringFactory.Build(Db()), r => r.CountryName, ct);", classFile);
        // Constructed with the preloaded cache field directly, not resolved via DI.
        Assert.Contains("var transform = new SyntheticLookupSingleTargetTransform(lKP_CountryCache);", classFile);
    }

    [Fact]
    public void Generate_WiresALookupThenAggregateFlow_ThroughAMulticast()
    {
        // SyntheticLookupThenAggregate.dtsx (2026-09-02) -- the real evidenced composed shape
        // (RBC_Demo_ETL's own DFT_LookupAndAggregate): Lookup (NoMatchBehavior=1/redirect) ->
        // Multicast -> {live branch -> Aggregate -> destination, discarded branch -> RowCount},
        // Lookup's own No-Match output -> a second discarded RowCount. Verified end-to-end
        // against real dtexec and a real generated run (see Tools/SsisExtractor/CLAUDE.md) --
        // this test pins the exact generated wiring so a regression is caught at unit-test
        // speed, not only by re-running the fixture by hand.
        var package = LoadSyntheticFixture("SyntheticLookupThenAggregate.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        // Every gap is advisory -- both discarded RowCount branches, plus the unavoidable
        // SqlCommand-column-naming and Notification advisories. Nothing blocks generation.
        Assert.DoesNotContain(result.Gaps, g => g.IsBlocking);
        Assert.Contains(result.Gaps, g => g.Reason.Contains("Multicast 'MCAST_Matched' output 'Multicast Output 2'") && g.Reason.Contains("User::MatchedRows"));
        Assert.Contains(result.Gaps, g => g.Reason.Contains("Lookup 'LKP_Country' output 'Lookup No Match Output'") && g.Reason.Contains("User::UnmatchedRows"));
        // A real, previously-silent gap caught 2026-09-06 by an independent review: this composed
        // flow used to reach this point with NO "Aggregate source" starter test AND no gap either
        // (unlike every other flow shape). The GroupBy value ("Region") only resolves through the
        // Lookup cache here, not a plain row property, so it correctly degrades to an honest
        // advisory rather than a naive reuse of the standalone Aggregate flow's own test template
        // (which was tried first and caught -- by actually building the generated test, not the
        // gap count -- emitting a CS0117 referencing a "Region" property that doesn't exist).
        Assert.Contains(result.Gaps, g => g.Kind == GapKind.TestOracle && g.Reason.Contains("Lookup-copied reference column"));
        Assert.DoesNotContain(result.SiblingFiles, f => f.RelativePath.EndsWith("AggregateRowSourceTests.cs"));

        var classFile = result.Files.Single(f => f.RelativePath == "SyntheticLookupThenAggregate.cs").Content;

        // The raw source is filtered by the lookup cache BEFORE it ever reaches grouping --
        // a Lookup miss must never be aggregated under a null/sentinel key.
        Assert.Contains("return new FilteringRowSource<AGG_ByRegionSourceSqlRow>(\"AGG_ByRegion\", BuildRawSource(), row => lKP_CountryCache.ContainsKey(row.Country));", classFile);
        // The GroupBy key itself reads through the SAME cache, not a plain row property --
        // "Region" only exists via the Lookup's own copied reference column.
        Assert.Contains("row => lKP_CountryCache[row.Country].Region,", classFile);
        // The Count column is untouched -- CustomerID is a plain raw column, no lookup involved.
        Assert.Contains("CustomerCount = rows.Count(r => r.CustomerID != null)", classFile);

        // The transform is constructed with NO argument -- unlike the plain single-output Lookup
        // shape, the cache is already fully consumed upstream by AggregateFlowSource itself, so a
        // constructor argument here would be a build error (no such constructor exists).
        Assert.Contains("var transform = new SyntheticLookupThenAggregateTargetTransform();", classFile);

        var transform = result.Files.Single(f => f.RelativePath == "Mapping/SyntheticLookupThenAggregateTargetTransform.cs").Content;
        Assert.Contains("Region = row.Region,", transform);
        Assert.Contains("CustomerCount = row.CustomerCount,", transform);
    }

    [Fact]
    public void Generate_SkipsADiscardedMulticastBranch_WithNoLookupOrAggregate()
    {
        // SyntheticMulticastDiscard.dtsx (2026-09-02): a plain Multicast, one live branch, one
        // discarded RowCount branch -- proves GenerateMulticastFlow's own defensive discard-skip
        // (added as a direct, foreseeable consequence of ResolveBranch now accepting a discard,
        // not the real motivating shape, which always reaches GenerateLookupThenAggregateFlow
        // instead since an Aggregate anywhere in the pipeline wins dispatch first).
        var package = LoadSyntheticFixture("SyntheticMulticastDiscard.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.DoesNotContain(result.Gaps, g => g.IsBlocking);
        Assert.Contains(result.Gaps, g => g.Reason.Contains("User::RowsSeen"));

        var classFile = result.Files.Single(f => f.RelativePath == "SyntheticMulticastDiscard.cs").Content;
        // Exactly one branch wired -- the discarded one contributes no ProgramMulticastBranch,
        // no entity, no transform, no table (and so no second ConditionalSplitBranch<> construction).
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(classFile, "new ConditionalSplitBranch<"));
    }

    [Fact]
    public void Generate_ReportsABlockingGap_ForAMulticastAndAggregateWithNoLookup()
    {
        // SyntheticMulticastAggregateSibling.dtsx (2026-09-06) -- a real, previously-SILENT
        // correctness bug found by an independent review, not by this project's own test suite:
        // flow.Aggregate and flow.Multicast are resolved entirely independently by
        // PackagePlanner, so a plain Multicast (no Lookup at all) with one branch straight to a
        // destination and a SECOND branch through an Aggregate to a DIFFERENT destination used
        // to fall straight into GenerateAggregateFlow -- which never reads flow.Multicast --
        // silently dropping the straight branch and wiring the Aggregate's own row against
        // whichever destination happened to resolve first. Confirmed, before this fix existed,
        // by actually building the resulting project: a real CS1061, reported as a fully-
        // generatable package (0 blocking gaps). Now gapped explicitly instead, matching the one
        // real evidenced Aggregate-behind-a-Multicast shape (always through a Lookup with exactly
        // one live branch, see Generate_WiresALookupThenAggregateFlow_ThroughAMulticast above),
        // which this fixture deliberately has none of.
        var package = LoadSyntheticFixture("SyntheticMulticastAggregateSibling.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        // Zero files for this flow -- not a half-wired Program.cs referencing a destination
        // that never got its own entity/transform.
        Assert.Empty(result.Files.Where(f => f.RelativePath.EndsWith(".cs") && !f.RelativePath.Contains("Shared")));
        var gap = Assert.Single(result.Gaps, g => g.Location == "DFT_MulticastAggregateSibling");
        Assert.True(gap.IsBlocking);
        Assert.Contains("Aggregate 'AGG_ByRegion' and Multicast 'MCAST_Split'", gap.Reason);
        Assert.Contains("with no Lookup involved", gap.Reason);
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
        // limitation (its Lookup routes both a match and a no-match output to real downstream
        // processing -- SyntheticLookupSplit's own No-Match output leads to a real destination,
        // not a discardable RowCount dead end, so both are "live" and this is still a hard gap),
        // rather than repeating a reason that no longer applies.
        Assert.DoesNotContain(result.Gaps, g => g.Reason.Contains("declares no join key"));
        Assert.Contains(result.Gaps, g => g.Reason.Contains("routes more than one output to real downstream processing"));
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

    [Fact]
    public void Generate_WiresALookupFlow_WhereTheNoMatchOutputIsTheLiveRoute()
    {
        // SyntheticLookupNoMatchIsLive.dtsx (Phase 3 of the gap-audit plan,
        // concurrent-whistling-turing.md, 2026-09-16) -- the classic "insert-if-new" dimension
        // pattern, confirmed real from two GitHub portfolio packages (author_dim.dtsx/
        // address_dim.dtsx): Lookup Match Output left completely unrouted, Lookup No Match Output
        // wired straight to the destination. Verified end-to-end against a real dtexec run and a
        // real generated run, exact row-for-row match (Bob/Dave inserted, Alice/Carol excluded) --
        // see CLAUDE.md's own account. This test pins the exact generated wiring.
        var package = LoadSyntheticFixture("SyntheticLookupNoMatchIsLive.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        // Every gap is advisory (the unavoidable SqlCommand-column-naming and Notification
        // advisories, plus the "no direct-invocation starter test" advisory for a flow whose
        // source reads a Lookup cache populated only by RunAsync's own bootstrap) -- nothing
        // blocks generation.
        Assert.DoesNotContain(result.Gaps, g => g.IsBlocking);

        var classFile = result.Files.Single(f => f.RelativePath == "SyntheticLookupNoMatchIsLive.cs").Content;
        // The cache is still preloaded (needed by the filter), even though the transform itself
        // never receives it.
        Assert.Contains("lKP_ExistingNamesCache = await LKP_ExistingNamesCache.LoadAsync<string>(SqlConnectionStringFactory.Build(Db()), r => r.ExistingName, ct);", classFile);
        // Every row reaching the destination must be a genuine MISS -- the mirror image of the
        // plain single-output Lookup shape's own ContainsKey check.
        Assert.Contains("return new FilteringRowSource<SyntheticLookupNoMatchIsLiveTargetSqlRow>(\"LKP_ExistingNames\", BuildRawSource(), row => !lKP_ExistingNamesCache.ContainsKey(row.Name));", classFile);
        // The transform is constructed with NO argument -- no reference column is ever copied for
        // this shape (a miss has no reference row to copy from), so a constructor argument here
        // would be a build error (no such constructor exists).
        Assert.Contains("var transform = new SyntheticLookupNoMatchIsLiveTargetTransform();", classFile);

        var transform = result.Files.Single(f => f.RelativePath == "Mapping/SyntheticLookupNoMatchIsLiveTargetTransform.cs").Content;
        // A plain passthrough transform -- no reference-column assignment anywhere, and no cache
        // constructor parameter.
        Assert.DoesNotContain("Dictionary<", transform);
        Assert.Contains("Name = row.Name,", transform);
    }
}
