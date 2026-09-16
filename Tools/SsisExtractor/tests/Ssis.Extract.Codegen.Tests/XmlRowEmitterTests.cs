using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

public class XmlRowEmitterTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Emit_ProducesEveryColumn_FromTheRealXmlSchema()
    {
        // SyntheticXmlSource.dtsx: Microsoft.XmlSourceAdapter (discriminated via
        // UserComponentTypeName -- ComponentClassId is the generic "Microsoft.ManagedComponentHost")
        // reading the real, checked-in synthetic-xml-source.xml/.xsd pair -- a small reproduction
        // of ETL-SSIS-Real-Scenarios' own UseCase_73 "XML Source" -- -> OLE DB Destination, no
        // transform. `id` resolves to ui2 (unsigned short, mapped to CLR `int` -- see
        // SsisPipelineTypeMap's own new "ui2" entry); every other column is a plain string.
        var package = LoadSyntheticFixture("SyntheticXmlSource.dtsx");
        var source = TestFixtures.FindComponent(package, "Microsoft.ManagedComponentHost", "XML Source");

        var result = XmlRowEmitter.Emit("Generated.Xml", "RecordRow", source);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Equal("Xml/RecordRow.cs", file.RelativePath);

        Assert.Contains("namespace Generated.Xml;", file.Content);
        Assert.Contains("public sealed class RecordRow", file.Content);
        Assert.Contains("public int id { get; set; }", file.Content);
        // Every string column is nullable, NOT `= ""` -- a measured, not guessed, difference
        // from every other row-type emitter (see this type's own doc comment): a real dtexec run
        // showed both an empty XML element and an entirely omitted optional one resolve to a
        // genuine NULL, never an empty string.
        Assert.Contains("public string? first_name { get; set; }", file.Content);
        Assert.Contains("public string? last_name { get; set; }", file.Content);
        Assert.Contains("public string? email { get; set; }", file.Content);
        Assert.Contains("public string? gender { get; set; }", file.Content);
        Assert.Contains("public string? country { get; set; }", file.Content);
        Assert.DoesNotContain("= \"\";", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_EndsInASingleTrailingNewline_NoCarriageReturnsAnywhere()
    {
        var package = LoadSyntheticFixture("SyntheticXmlSource.dtsx");
        var source = TestFixtures.FindComponent(package, "Microsoft.ManagedComponentHost", "XML Source");

        var file = Assert.Single(XmlRowEmitter.Emit("Generated.Xml", "RecordRow", source).Files);

        Assert.DoesNotContain('\r', file.Content);
        Assert.EndsWith("\n", file.Content);
        Assert.False(file.Content.EndsWith("\n\n", StringComparison.Ordinal));
    }
}
