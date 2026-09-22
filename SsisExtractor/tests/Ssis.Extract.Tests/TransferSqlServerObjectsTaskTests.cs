using System.Xml.Linq;
using Ssis.Extract.Dtsx;

namespace Ssis.Extract.Tests;

/// <summary>
/// Covers <see cref="DtsxPackageReader.ReadTransferSqlServerObjectsTask"/> and
/// <see cref="DtsxPackageReader.ParseTransferTablesList"/> against hand-built XML matching the
/// real, empirically-confirmed shape read directly from a genuine SSDT-authored package
/// (<c>D:\PoC\SSIS_Packages_From_GitHub\ETL-SSIS-Real-Scenarios\UseCase_55\...\Package.dtsx</c>,
/// "Transfer SQL Server Objects Task") -- see <c>TransferSqlServerObjectsTaskPayload</c>'s own
/// doc comment for the exact real attribute values these tests are built from.
/// </summary>
public class TransferSqlServerObjectsTaskTests
{
    [Fact]
    public void ReadTransferSqlServerObjectsTask_ReadsTheRealEvidencedShape_AndResolvesBothConnections()
    {
        var objectData = new XElement("ObjectData",
            new XElement("TransferSqlServerObjectsTaskData",
                new XAttribute("SourceConnection", "{0068FB23-8DF9-447A-AD0B-DED0CBFED5CC}"),
                new XAttribute("DestinationConnection", "{05F4CEB5-52A8-4CD0-894B-4B7E6D2A5099}"),
                new XAttribute("SourceDatabase", "SSIS"),
                new XAttribute("DestinationDatabase", "test"),
                new XAttribute("TablesList", "4,15,[dbo].[Country],16,[dbo].[Currency],17,[dbo].[customer1],17,[dbo].[customer2],"),
                new XAttribute("DropObjectsFirst", "True"),
                new XAttribute("IncludeDependentObjects", "True"),
                new XAttribute("CopyData", "True"),
                new XAttribute("CopyIndexes", "True"),
                new XAttribute("CopyPrimaryKeys", "True"),
                new XAttribute("CopyForeignKeys", "True")));
        var cmDtsIdToName = new Dictionary<string, string>
        {
            ["{0068FB23-8DF9-447A-AD0B-DED0CBFED5CC}"] = ".",
            ["{05F4CEB5-52A8-4CD0-894B-4B7E6D2A5099}"] = "AMR\\MSSQLSERVER01",
        };

        var payload = DtsxPackageReader.ReadTransferSqlServerObjectsTask(objectData, cmDtsIdToName);

        Assert.Equal("{0068FB23-8DF9-447A-AD0B-DED0CBFED5CC}", payload.SourceConnectionRefRaw);
        Assert.Equal(".", payload.SourceConnectionName);
        Assert.Equal("{05F4CEB5-52A8-4CD0-894B-4B7E6D2A5099}", payload.DestinationConnectionRefRaw);
        Assert.Equal("AMR\\MSSQLSERVER01", payload.DestinationConnectionName);
        Assert.Equal("SSIS", payload.SourceDatabase);
        Assert.Equal("test", payload.DestinationDatabase);
        Assert.Equal(["[dbo].[Country]", "[dbo].[Currency]", "[dbo].[customer1]", "[dbo].[customer2]"], payload.Tables);
        Assert.True(payload.DropObjectsFirst);
        Assert.True(payload.IncludeDependentObjects);
        Assert.True(payload.CopyData);
        Assert.True(payload.CopyIndexes);
        Assert.True(payload.CopyPrimaryKeys);
        Assert.True(payload.CopyForeignKeys);
    }

    [Fact]
    public void ReadTransferSqlServerObjectsTask_LeavesConnectionNamesNull_WhenReferencesAreDangling()
    {
        var objectData = new XElement("ObjectData",
            new XElement("TransferSqlServerObjectsTaskData",
                new XAttribute("SourceConnection", "{00000000-0000-0000-0000-000000000000}"),
                new XAttribute("DestinationConnection", "{11111111-1111-1111-1111-111111111111}")));

        var payload = DtsxPackageReader.ReadTransferSqlServerObjectsTask(objectData, []);

        Assert.Equal("{00000000-0000-0000-0000-000000000000}", payload.SourceConnectionRefRaw);
        Assert.Null(payload.SourceConnectionName);
        Assert.Equal("{11111111-1111-1111-1111-111111111111}", payload.DestinationConnectionRefRaw);
        Assert.Null(payload.DestinationConnectionName);
    }

    [Fact]
    public void ReadTransferSqlServerObjectsTask_ReturnsEmptyPayload_WhenTheElementIsMissing()
    {
        var objectData = new XElement("ObjectData");

        var payload = DtsxPackageReader.ReadTransferSqlServerObjectsTask(objectData, []);

        Assert.Null(payload.SourceDatabase);
        Assert.Null(payload.DestinationDatabase);
        Assert.Empty(payload.Tables);
        Assert.Null(payload.DropObjectsFirst);
    }

    [Fact]
    public void ReadTransferSqlServerObjectsTask_LeavesFlagsNull_WhenNotSetInTheXml()
    {
        var objectData = new XElement("ObjectData",
            new XElement("TransferSqlServerObjectsTaskData",
                new XAttribute("SourceDatabase", "Src"),
                new XAttribute("DestinationDatabase", "Dst")));

        var payload = DtsxPackageReader.ReadTransferSqlServerObjectsTask(objectData, []);

        Assert.Null(payload.DropObjectsFirst);
        Assert.Null(payload.IncludeDependentObjects);
        Assert.Null(payload.CopyData);
        Assert.Null(payload.CopyIndexes);
        Assert.Null(payload.CopyPrimaryKeys);
        Assert.Null(payload.CopyForeignKeys);
        Assert.Null(payload.TablesListRaw);
        Assert.Empty(payload.Tables);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseTransferTablesList_ReturnsEmpty_ForNullOrEmpty(string? raw)
    {
        Assert.Empty(DtsxPackageReader.ParseTransferTablesList(raw));
    }

    [Fact]
    public void ParseTransferTablesList_ReturnsEmpty_ForZeroTables()
    {
        Assert.Empty(DtsxPackageReader.ParseTransferTablesList("0,"));
    }

    [Fact]
    public void ParseTransferTablesList_DecodesByLength_NotByCommaSplit_SoAnEmbeddedCommaSurvives()
    {
        // A delimited object name legally containing a comma is unevidenced in this tool's own
        // corpus, but the length-prefix encoding this format actually uses handles it correctly
        // regardless -- proving the decoder isn't secretly just a comma split, which would have
        // happened to pass every other test here too.
        var raw = "1,11,[dbo].[A,B],";

        var tables = DtsxPackageReader.ParseTransferTablesList(raw);

        Assert.Equal(["[dbo].[A,B]"], tables);
    }

    [Fact]
    public void ParseTransferTablesList_StopsGracefully_ForATruncatedOrMalformedList_RatherThanThrowing()
    {
        // Declares 4 tables but only supplies enough text for 2 -- this tool only ever reports on
        // this task, never executes it, so a decode quirk in one attribute must not throw and
        // abort extraction of the rest of the package.
        var raw = "4,15,[dbo].[Country],16,[dbo].[Currency],";

        var tables = DtsxPackageReader.ParseTransferTablesList(raw);

        Assert.Equal(["[dbo].[Country]", "[dbo].[Currency]"], tables);
    }

    [Fact]
    public void ParseTransferTablesList_ReturnsEmpty_ForNonNumericLeadingCount()
    {
        Assert.Empty(DtsxPackageReader.ParseTransferTablesList("not-a-number,15,[dbo].[X],"));
    }
}
