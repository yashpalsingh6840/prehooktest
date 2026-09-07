namespace Ssis.Extract.Codegen.Tests;

public class SqlStatementBuilderEmitterTests
{
    [Fact]
    public void Emit_ProducesANamedStaticClass_WithTheSqlTextAsAPlainReturnExpression()
    {
        var result = SqlStatementBuilderEmitter.Emit("Generated.Mapping", "SQL_PostLoad", "UPDATE dbo.Target SET Name = UPPER(Name);");

        var file = Assert.Single(result.Files);
        Assert.Equal("Mapping/SQL_PostLoadStatement.cs", file.RelativePath);
        Assert.Contains("namespace Generated.Mapping;", file.Content);
        Assert.Contains("public static class SQL_PostLoadStatement", file.Content);
        Assert.Contains(
            "public static string BuildStatement() => \"UPDATE dbo.Target SET Name = UPPER(Name);\";",
            file.Content);
        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_RoutesTheSqlTextThroughCSharpStringLiteral_SoQuotesAndNewlinesStayValidCSharp()
    {
        // Proves SqlStatementBuilderEmitter actually escapes the sql text (via
        // ProgramEmitter.CSharpStringLiteral, tested in its own right elsewhere) rather than
        // splicing it in raw -- a raw double-quote or newline spliced directly into the literal
        // would be a C# compile error (CS1010/CS1002), not just a wrong value, so
        // AssertNoSyntaxErrors is the meaningful check here.
        var result = SqlStatementBuilderEmitter.Emit(
            "Generated.Mapping", "SQL_Insert", "INSERT INTO dbo.Log (Msg) VALUES (N'quote\" here')\r\nGO");

        var file = Assert.Single(result.Files);
        CodeAssertions.AssertNoSyntaxErrors(file.Content);
        Assert.Contains(
            @"public static string BuildStatement() => ""INSERT INTO dbo.Log (Msg) VALUES (N'quote\"" here')\r\nGO"";",
            file.Content);
    }

    [Fact]
    public void Emit_ReportsNoGaps()
    {
        var result = SqlStatementBuilderEmitter.Emit("Generated.Mapping", "SQL_Task", "SELECT 1;");

        Assert.Empty(result.Gaps);
    }
}
