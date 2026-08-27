using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Unit-level coverage for the ADO NET Source/Destination normalization helpers
/// (AdoNetSupport/DestinationInfo/SourceInfo). A full round-trip synthetic fixture
/// (OLE DB/ADO NET Source -&gt; Derived Column -&gt; ADO NET Destination, built end to end and
/// actually run, matching the discipline every other gap this session closed used) could NOT
/// be built: constructing an ADO.NET connection manager through the real SSIS object model
/// fails with <c>DtsCouldNotCreateManagedConnectionException: "Could not create a managed
/// connection manager"</c> in this environment, reproduced identically across three provider/
/// connection-string variations (System.Data.SqlClient and Microsoft.Data.SqlClient, both
/// pre-loaded and not, SSPI and explicit-True Integrated Security) with no further diagnostic
/// detail surfaced by the SSIS runtime -- see CLAUDE.md's "ADO NET Source/Destination" section
/// for the full account. These tests instead pin down the one piece of genuinely new logic
/// (table-name normalization and OLE DB/ADO NET dispatch) directly; the planner's own
/// component-discovery and gap-reporting behavior is proven end to end against the real
/// RBC_Demo_ETL package instead (DFT_AdoNetRoundTrip's structural gap disappeared entirely
/// after this change -- see CLAUDE.md).
/// </summary>
public class AdoNetSupportTests
{
    [Theory]
    [InlineData("\"dbo\".\"CustomerExportLog\"", "[dbo].[CustomerExportLog]")]
    [InlineData("\"Sales\".\"Order\"", "[Sales].[Order]")]
    public void NormalizeTableName_ConvertsDoubleQuotedFormToBracketForm(string adoNetForm, string expected)
    {
        Assert.Equal(expected, AdoNetSupport.NormalizeTableName(adoNetForm));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dbo.CustomerExportLog")] // already bracket/plain form, not ADO NET's own quoting
    [InlineData("\"dbo\"")] // only one part -- no ".\"" separator
    [InlineData("\"\".\"Table\"")] // empty schema
    public void NormalizeTableName_ReturnsNull_ForAnythingNotTheEvidencedTwoPartQuotedShape(string? malformed)
    {
        Assert.Null(AdoNetSupport.NormalizeTableName(malformed));
    }

    [Fact]
    public void DestinationInfo_PrefersOleDbDestination_WhenBothAreSomehowPresent()
    {
        // Not a real shape (a component is one or the other, never both), but proves the
        // dispatch order is deterministic rather than accidental.
        var component = NewComponent(
            oleDb: new OleDbDestinationPayload { ConnectionName = "CM_Ole", OpenRowset = "[dbo].[Ole]" },
            adoNet: new AdoNetDestinationPayload { ConnectionName = "CM_Ado", TableOrViewName = "\"dbo\".\"Ado\"" });

        Assert.Equal("CM_Ole", DestinationInfo.ConnectionName(component));
        Assert.Equal("[dbo].[Ole]", DestinationInfo.TableName(component));
    }

    [Fact]
    public void DestinationInfo_ResolvesFromAdoNetDestination_WhenNoOleDbDestinationIsPresent()
    {
        var component = NewComponent(adoNet: new AdoNetDestinationPayload
        {
            ConnectionName = "CM_ADO_SSISDemo",
            TableOrViewName = "\"dbo\".\"CustomerExportLog\"",
            UseBulkInsertWhenPossible = true,
        });

        Assert.Equal("CM_ADO_SSISDemo", DestinationInfo.ConnectionName(component));
        Assert.Equal("[dbo].[CustomerExportLog]", DestinationInfo.TableName(component));
        Assert.True(DestinationInfo.IsFastLoadConfigured(component));
    }

    [Fact]
    public void DestinationInfo_IsFastLoadConfigured_IsFalse_WhenAdoNetDestinationDoesNotUseBulkInsert()
    {
        var component = NewComponent(adoNet: new AdoNetDestinationPayload
        {
            ConnectionName = "CM_ADO_SSISDemo",
            TableOrViewName = "\"dbo\".\"CustomerExportLog\"",
            UseBulkInsertWhenPossible = false,
        });

        Assert.False(DestinationInfo.IsFastLoadConfigured(component));
    }

    [Fact]
    public void SourceInfo_IsSqlSource_IsTrue_ForAnAdoNetSourceComponent()
    {
        // The exact shape RBC_Demo_ETL's own ADO_SRC_Customers extracts to: AccessMode=1 with
        // SqlCommand populated and TableOrViewName empty (confirmed real, not guessed -- see
        // AdoNetSourcePayload's own doc comment).
        var component = NewComponent(adoNetSource: new AdoNetSourcePayload
        {
            ConnectionName = "CM_ADO_SSISDemo",
            SqlCommand = "SELECT CustomerID, FullName FROM dbo.StagingCustomers WHERE IsValidRow = 1",
            TableOrViewName = null,
            AccessMode = 1,
        });

        Assert.True(SourceInfo.IsSqlSource(component));
        Assert.Equal("CM_ADO_SSISDemo", SourceInfo.ConnectionName(component));
    }

    [Fact]
    public void SourceInfo_IsSqlSource_IsFalse_ForAPlainDerivedColumnComponent()
    {
        var component = NewComponent();

        Assert.False(SourceInfo.IsSqlSource(component));
        Assert.Null(SourceInfo.ConnectionName(component));
    }

    private static PipelineComponentSpec NewComponent(
        OleDbDestinationPayload? oleDb = null, AdoNetDestinationPayload? adoNet = null, AdoNetSourcePayload? adoNetSource = null) =>
        new()
        {
            RefId = "Package\\DFT_Test\\Component",
            Name = "Component",
            ComponentClassId = "Microsoft.ManagedComponentHost",
            OleDbDestination = oleDb,
            AdoNetDestination = adoNet,
            AdoNetSource = adoNetSource,
        };
}
