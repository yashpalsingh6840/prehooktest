using System.Xml.Linq;
using Ssis.Extract.Dtsx;

namespace Ssis.Extract.Tests;

/// <summary>
/// Covers <see cref="PipelineReader.ReadComponent"/>'s legacy-ID normalization (via
/// <c>LegacyComponentIds</c>) directly against hand-built XML, the same "no synthetic fixture
/// easily produces this shape" reasoning already used for the DPAPI-ciphertext connection
/// manager tests -- no real or synthetic .dtsx in this repo carries a legacy-spelled
/// componentClassID (this PoC's own packages and every fixture were built against a current
/// SSIS version), so a hand-built element is the only way to exercise this path at all.
/// </summary>
public class LegacyComponentIdsTests
{
    private static XElement MinimalComponent(string componentClassId, string refId = "C1", string name = "Comp") =>
        new("component",
            new XAttribute("refId", refId),
            new XAttribute("name", name),
            new XAttribute("componentClassID", componentClassId));

    [Theory]
    [InlineData("DTSAdapter.OLEDBSource.1", "Microsoft.OLEDBSource")]
    [InlineData("DTSAdapter.OLEDBSource.2", "Microsoft.OLEDBSource")]
    [InlineData("DTSAdapter.FlatFileSource.2", "Microsoft.FlatFileSource")]
    [InlineData("DTSTransform.DerivedColumn.2", "Microsoft.DerivedColumn")]
    [InlineData("DTSTransform.ConditionalSplit.2", "Microsoft.ConditionalSplit")]
    [InlineData("DTSTransform.UnionAll.2", "Microsoft.UnionAll")]
    [InlineData("DTSTransform.Lookup", "Microsoft.Lookup")]
    [InlineData("DTSTransform.Lookup.4", "Microsoft.Lookup")]
    [InlineData("DTSTransform.Aggregate.1", "Microsoft.Aggregate")]
    [InlineData("DTSTransform.Sort.1", "Microsoft.Sort")]
    [InlineData("DTSTransform.Merge.4", "Microsoft.Merge")]
    [InlineData("DTSTransform.MergeJoin.4", "Microsoft.MergeJoin")]
    [InlineData("DTSTransform.Multicast.4", "Microsoft.Multicast")]
    public void ReadComponent_NormalizesAKnownLegacyProgId_AndKeepsTheRawValue(string legacy, string canonical)
    {
        var comp = PipelineReader.ReadComponent(MinimalComponent(legacy), []);

        Assert.Equal(canonical, comp.ComponentClassId);
        Assert.Equal(legacy, comp.RawComponentClassId);
        Assert.False(comp.IsUnresolvedLegacyClsid);
    }

    [Theory]
    [InlineData("{D23FD76B-F51D-420F-BBCB-19CBF6AC1AB4}", "Microsoft.FlatFileSource")]
    [InlineData("{8DA75FED-1B7C-407D-B2AD-2B24209CCCA4}", "Microsoft.FlatFileDestination")]
    [InlineData("{671046B0-AA63-4C9F-90E4-C06E0B710CE3}", "Microsoft.Lookup")]
    public void ReadComponent_NormalizesAKnownClsid_AndKeepsTheRawValue(string clsid, string canonical)
    {
        var comp = PipelineReader.ReadComponent(MinimalComponent(clsid), []);

        Assert.Equal(canonical, comp.ComponentClassId);
        Assert.Equal(clsid, comp.RawComponentClassId);
        Assert.False(comp.IsUnresolvedLegacyClsid);
    }

    [Fact]
    public void ReadComponent_LeavesAnAlreadyCanonicalComponent_Untouched()
    {
        var comp = PipelineReader.ReadComponent(MinimalComponent("Microsoft.OLEDBSource"), []);

        Assert.Equal("Microsoft.OLEDBSource", comp.ComponentClassId);
        Assert.Null(comp.RawComponentClassId);
        Assert.False(comp.IsUnresolvedLegacyClsid);
    }

    [Fact]
    public void ReadComponent_ReportsAnUnresolvedClsid_AsSuch_RatherThanGuessing()
    {
        // A real-shaped CLSID this table has no evidence for -- must NOT be silently
        // normalized to anything, and must NOT be treated the same as a genuine third-party
        // component (RulesEngine routes these two cases to different findings).
        var comp = PipelineReader.ReadComponent(MinimalComponent("{00000000-0000-0000-0000-000000000000}"), []);

        Assert.Equal("{00000000-0000-0000-0000-000000000000}", comp.ComponentClassId);
        Assert.Null(comp.RawComponentClassId);
        Assert.True(comp.IsUnresolvedLegacyClsid);
    }

    [Fact]
    public void ReadComponent_DoesNotTreatAnOrdinaryThirdPartyProgId_AsAnUnresolvedClsid()
    {
        // "Acme.CustomTransform" is a real third-party component (per the existing
        // ThirdPartyComponent_IsFlagged RulesEngineTests case) -- it must still be reported that
        // way, not swept into the CLSID-shaped "unknown legacy component" bucket.
        var comp = PipelineReader.ReadComponent(MinimalComponent("Acme.CustomTransform"), []);

        Assert.Equal("Acme.CustomTransform", comp.ComponentClassId);
        Assert.Null(comp.RawComponentClassId);
        Assert.False(comp.IsUnresolvedLegacyClsid);
    }

    [Theory]
    [InlineData("DTSAdapter.SomeUnknownAdapter.1")]
    [InlineData("DTSTransform.SomeUnknownTransform.3")]
    public void TryNormalize_LeavesAnUnrecognizedVersionedProgId_Unchanged_RatherThanGuessing(string raw)
    {
        var normalized = LegacyComponentIds.TryNormalize(raw, out var canonical);

        Assert.False(normalized);
        Assert.Equal(raw, canonical);
    }
}
