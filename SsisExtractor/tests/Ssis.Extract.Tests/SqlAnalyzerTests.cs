using Ssis.Extract.Sql;

namespace Ssis.Extract.Tests;

/// <summary>
/// Unit tests for <see cref="SqlAnalyzer"/> (plan §5.4). The two PoC packages between them
/// contain exactly one shape of SQL (<c>TRUNCATE TABLE</c>), so everything else here is
/// synthetic -- and the read/write disambiguation in particular needs to be, because
/// ScriptDom models an <c>INSERT INTO x</c> target as a <c>NamedTableReference</c> just like
/// a <c>FROM</c> source, which is the exact trap this analyzer has to avoid.
/// </summary>
public class SqlAnalyzerTests
{
    [Fact]
    public void TruncateTable_IsAWriteNotARead()
    {
        // The PoC's own SQL, verbatim.
        var result = SqlAnalyzer.Analyze("P", "loc", "TRUNCATE TABLE dbo.Employee;");

        Assert.True(result.ParsedSuccessfully);
        Assert.True(result.HasTruncate);
        Assert.Equal(["dbo.Employee"], result.WritesTo);
        Assert.Empty(result.ReadsFrom);
        Assert.Equal(["TruncateTableStatement"], result.StatementTypes);
    }

    [Fact]
    public void MultipleStatements_AreAllCaptured()
    {
        var result = SqlAnalyzer.Analyze("P", "loc", "TRUNCATE TABLE dbo.Department; TRUNCATE TABLE dbo.Designation;");

        Assert.Equal(["dbo.Department", "dbo.Designation"], result.WritesTo);
    }

    [Fact]
    public void InsertSelect_SeparatesTargetFromSources()
    {
        var result = SqlAnalyzer.Analyze("P", "loc",
            "INSERT INTO dbo.Target (a) SELECT a FROM dbo.Source JOIN dbo.Other o ON o.id = dbo.Source.id;");

        Assert.Equal(["dbo.Target"], result.WritesTo);
        // dbo.Target must NOT appear here even though ScriptDom sees it as a table reference.
        Assert.Equal(["dbo.Other", "dbo.Source"], result.ReadsFrom);
    }

    [Fact]
    public void Merge_FlagsMergeAndSeparatesTargetFromUsingSource()
    {
        var result = SqlAnalyzer.Analyze("P", "loc",
            "MERGE dbo.Tgt AS t USING dbo.Src AS s ON t.id = s.id WHEN MATCHED THEN UPDATE SET t.v = s.v;");

        Assert.True(result.HasMerge);
        Assert.Equal(["dbo.Tgt"], result.WritesTo);
        Assert.Equal(["dbo.Src"], result.ReadsFrom);
    }

    [Fact]
    public void Delete_IsFlaggedAndCountsAsAWrite()
    {
        var result = SqlAnalyzer.Analyze("P", "loc", "DELETE FROM dbo.Thing WHERE x = 1;");

        Assert.True(result.HasDelete);
        Assert.Equal(["dbo.Thing"], result.WritesTo);
    }

    [Fact]
    public void ExecProcedure_IsCaptured()
    {
        var result = SqlAnalyzer.Analyze("P", "loc", "EXEC dbo.usp_DoThing @a = 1;");

        Assert.Equal(["dbo.usp_DoThing"], result.ExecutesProcedures);
        Assert.False(result.HasDynamicSql);
    }

    [Theory]
    [InlineData("EXEC sp_executesql N'SELECT 1';")]
    [InlineData("EXEC('SELECT * FROM ' + @tbl);")]
    public void DynamicSql_IsFlagged_InBothItsForms(string sql)
    {
        var result = SqlAnalyzer.Analyze("P", "loc", sql);

        Assert.True(result.HasDynamicSql);
    }

    [Fact]
    public void UnqualifiedName_IsNotSilentlyGivenADboSchema()
    {
        // Defaulting to dbo would fabricate a fact -- the real default schema depends on the
        // executing login, which extraction cannot know.
        var result = SqlAnalyzer.Analyze("P", "loc", "SELECT * FROM Employee;");

        Assert.Equal(["Employee"], result.ReadsFrom);
    }

    [Fact]
    public void UnparseableSql_IsReportedNotSwallowed()
    {
        var result = SqlAnalyzer.Analyze("P", "loc", "BEGIN SELECT FROM WHERE ((( ;");

        Assert.False(result.ParsedSuccessfully);
        Assert.NotEmpty(result.ParseErrors);
        Assert.Empty(result.WritesTo);
    }

    [Fact]
    public void EmptySql_IsSuccessNotFailure()
    {
        var result = SqlAnalyzer.Analyze("P", "loc", "   ");

        Assert.True(result.ParsedSuccessfully);
        Assert.Empty(result.ParseErrors);
        Assert.Empty(result.StatementTypes);
    }
}
