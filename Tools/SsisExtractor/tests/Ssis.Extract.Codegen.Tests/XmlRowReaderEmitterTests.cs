using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

public class XmlRowReaderEmitterTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Emit_ReadsEveryColumnByName_NotByPosition()
    {
        // A deliberate, genuine improvement over ExcelRowReaderEmitter's own positional read, not
        // a blind copy of it -- System.Xml.Linq's XElement.Element(name) has no equivalent of
        // IExcelDataReader's own missing GetOrdinal(name), so there is no reason to read by
        // position here. The non-string "id" column parses via int.Parse against its own element
        // text; every string column collapses BOTH a missing and an empty element to null
        // (measured real behaviour, see XmlRowReaderEmitter's own doc comment).
        var package = LoadSyntheticFixture("SyntheticXmlSource.dtsx");
        var source = TestFixtures.FindComponent(package, "Microsoft.ManagedComponentHost", "XML Source");

        var result = XmlRowReaderEmitter.Emit("Generated.Xml", "RecordRow", source);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Equal("Xml/RecordRowReader.cs", file.RelativePath);

        Assert.Contains("using System.Xml.Linq;", file.Content);
        Assert.Contains("public static class RecordRowReader", file.Content);
        Assert.Contains("public static RecordRow Read(XElement element) => new()", file.Content);
        Assert.Contains("id = int.Parse(element.Element(\"id\")!.Value, System.Globalization.CultureInfo.InvariantCulture),", file.Content);
        Assert.Contains("first_name = ReadNullableText(element.Element(\"first_name\")),", file.Content);
        Assert.Contains("last_name = ReadNullableText(element.Element(\"last_name\")),", file.Content);
        Assert.Contains("email = ReadNullableText(element.Element(\"email\")),", file.Content);
        Assert.Contains("gender = ReadNullableText(element.Element(\"gender\")),", file.Content);
        Assert.Contains("country = ReadNullableText(element.Element(\"country\")),", file.Content);
        Assert.Contains("private static string? ReadNullableText(XElement? element)", file.Content);
        Assert.Contains("return string.IsNullOrEmpty(value) ? null : value;", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }
}
