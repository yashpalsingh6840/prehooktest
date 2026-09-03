using System.Text.Json;
using Ssis.Validate.Harness;

namespace Ssis.Validate.Harness.Tests;

/// <summary>
/// <see cref="ConfigGenerator"/>'s primary-key auto-fill -- closes the gap
/// docs/gate3-harness.md names ("KeyColumn is hand-filled, not detected") now that
/// `ssisx report` produces primary-keys.json. No database needed: both inputs are plain
/// JSON files this test writes to a temp directory.
/// </summary>
public class ConfigGeneratorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cfg-gen-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteJson(string name, string json)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void KeyColumn_IsAutoFilled_WhenExactlyOneConfidentCandidateExists()
    {
        var nonDet = WriteJson("nd.json", "[]");
        var pk = WriteJson("pk.json", """
            [{"PackageName":"LoadEmployees","TargetTable":"[dbo].[Employee]","Columns":["EmployeeID"],"Confidence":"NamingConvention"}]
            """);

        var config = ConfigGenerator.Generate("LoadEmployees", "schema.sql", ["dbo.Employee"], nonDet, pk);

        Assert.Equal("EmployeeID", config.TargetTables[0].KeyColumn);
    }

    [Fact]
    public void KeyColumn_StaysNull_WhenConfidenceIsUnknown()
    {
        var nonDet = WriteJson("nd.json", "[]");
        var pk = WriteJson("pk.json", """
            [{"PackageName":"LoadEmployees","TargetTable":"[dbo].[Employee]","Columns":[],"Confidence":"Unknown"}]
            """);

        var config = ConfigGenerator.Generate("LoadEmployees", "schema.sql", ["dbo.Employee"], nonDet, pk);

        Assert.Null(config.TargetTables[0].KeyColumn);
    }

    [Fact]
    public void KeyColumn_StaysNull_WhenCandidateIsComposite()
    {
        // A composite key (>1 column) isn't representable by this config's single
        // KeyColumn field -- must be left for a human, not silently truncated to the first
        // column of a multi-column candidate.
        var nonDet = WriteJson("nd.json", "[]");
        var pk = WriteJson("pk.json", """
            [{"PackageName":"LoadEmployees","TargetTable":"[dbo].[Employee]","Columns":["A","B"],"Confidence":"NamingConvention"}]
            """);

        var config = ConfigGenerator.Generate("LoadEmployees", "schema.sql", ["dbo.Employee"], nonDet, pk);

        Assert.Null(config.TargetTables[0].KeyColumn);
    }

    [Fact]
    public void KeyColumn_StaysNull_WhenPrimaryKeysJsonPathIsOmitted()
    {
        var nonDet = WriteJson("nd.json", "[]");

        var config = ConfigGenerator.Generate("LoadEmployees", "schema.sql", ["dbo.Employee"], nonDet, primaryKeysJsonPath: null);

        Assert.Null(config.TargetTables[0].KeyColumn);
    }

    [Fact]
    public void KeyColumn_MatchesByBracketNormalizedTableName()
    {
        // primary-keys.json writes bracketed table names ("[dbo].[Employee]"); this tool's
        // own config uses plain ones ("dbo.Employee") -- must join across that difference,
        // not just an exact string match.
        var nonDet = WriteJson("nd.json", "[]");
        var pk = WriteJson("pk.json", """
            [{"PackageName":"LoadEmployees","TargetTable":"[dbo].[Employee]","Columns":["EmployeeID"],"Confidence":"NamingConvention"}]
            """);

        var config = ConfigGenerator.Generate("LoadEmployees", "schema.sql", ["dbo.Employee"], nonDet, pk);

        Assert.Equal("EmployeeID", config.TargetTables[0].KeyColumn);
    }
}
