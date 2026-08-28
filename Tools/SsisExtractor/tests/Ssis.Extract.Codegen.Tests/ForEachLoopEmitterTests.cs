namespace Ssis.Extract.Codegen.Tests;

public class ForEachLoopEmitterTests
{
    [Fact]
    public void TranslateSqlTemplate_TranslatesTheRealFELSampleFilesExpression()
    {
        // The exact real evidenced expression from RBC_Demo_ETL's own
        // FEL_SampleFiles/SQL_LogFileName -- confirmed via a direct read of Package_Advanced.dtsx.
        const string raw = "\"INSERT INTO dbo.EtlRunLog (PackageName, SourceName, EventType, MessageText) VALUES " +
            "(N'Package_Advanced', N'FEL_SampleFiles', N'FileFound', N'\" + @[User::CurrentFile] + \"');\"";

        var result = ForEachLoopEmitter.TranslateSqlTemplate(raw, "User::CurrentFile", "currentFile");

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal(
            "\"INSERT INTO dbo.EtlRunLog (PackageName, SourceName, EventType, MessageText) VALUES " +
            "(N'Package_Advanced', N'FEL_SampleFiles', N'FileFound', N'\" + currentFile + \"');\"",
            ok.CSharpExpression);
    }

    [Fact]
    public void TranslateSqlTemplate_DegradesToAGap_WhenTheExpressionReferencesADifferentVariable()
    {
        const string raw = "\"x\" + @[User::SomeOtherVariable]";

        var result = ForEachLoopEmitter.TranslateSqlTemplate(raw, "User::CurrentFile", "currentFile");

        var gap = Assert.IsType<NotTranslatable>(result);
        Assert.Contains("User::SomeOtherVariable", gap.Reason);
        Assert.Contains("not this loop's own mapped variable", gap.Reason);
    }

    [Fact]
    public void TranslateSqlTemplate_DegradesToAGap_ForAnUnsupportedExpressionShape()
    {
        // A function call has no representation in this deliberately narrow translator --
        // only string literals, the loop's own variable, and '+' concatenation are supported.
        const string raw = "UPPER(@[User::CurrentFile])";

        var result = ForEachLoopEmitter.TranslateSqlTemplate(raw, "User::CurrentFile", "currentFile");

        var gap = Assert.IsType<NotTranslatable>(result);
        Assert.Contains("unsupported expression shape", gap.Reason);
    }

    [Fact]
    public void TranslateSqlTemplate_ReturnsJustTheVariable_WhenTheWholeExpressionIsTheVariable()
    {
        var result = ForEachLoopEmitter.TranslateSqlTemplate("@[User::CurrentFile]", "User::CurrentFile", "currentFile");

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("currentFile", ok.CSharpExpression);
    }
}
