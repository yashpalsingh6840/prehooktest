using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen.Tests;

public class LookupCacheEmitterTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Emit_ProducesAReferenceRowAndAGenericLoadAsync_FromTheRealLookupPayload()
    {
        var package = LoadSyntheticFixture("SyntheticLookupSplit.dtsx");
        var lookup = TestFixtures.FindComponent(package, "Microsoft.Lookup", "Lookup");

        var result = LookupCacheEmitter.Emit("Generated.Mapping", "LookupCache", lookup);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Equal("Mapping/LookupCache.cs", file.RelativePath);

        Assert.Contains("public sealed class ReferenceRow", file.Content);
        Assert.Contains("public int CustomerID { get; set; }", file.Content);
        Assert.Contains("public string CustomerName { get; set; } = \"\";", file.Content);
        Assert.Contains("public string Region { get; set; } = \"\";", file.Content);

        // Read by position (0, 1, 2), not by name -- LookupPayload carries no independent
        // column ordinal beyond the SqlCommand's own SELECT list order.
        Assert.Contains("CustomerID = reader.GetFieldValue<int>(0),", file.Content);
        Assert.Contains("CustomerName = reader.GetFieldValue<string>(1),", file.Content);
        Assert.Contains("Region = reader.GetFieldValue<string>(2),", file.Content);

        Assert.Contains("public static async Task<Dictionary<TKey, ReferenceRow>> LoadAsync<TKey>(", file.Content);
        Assert.Contains("command.CommandText = \"SELECT CustomerID, CustomerName, Region FROM dbo.SyntheticCustomer\";", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_EndsInASingleTrailingNewline_NoCarriageReturnsAnywhere()
    {
        var package = LoadSyntheticFixture("SyntheticLookupSplit.dtsx");
        var lookup = TestFixtures.FindComponent(package, "Microsoft.Lookup", "Lookup");

        var file = Assert.Single(LookupCacheEmitter.Emit("Generated.Mapping", "LookupCache", lookup).Files);

        Assert.DoesNotContain('\r', file.Content);
        Assert.EndsWith("\n", file.Content);
        Assert.False(file.Content.EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Emit_ReportsAGap_WhenCacheTypeIsNotFull()
    {
        var partialCacheLookup = new PipelineComponentSpec
        {
            RefId = "Package\\DFT_Test\\Lookup",
            Name = "Lookup",
            ComponentClassId = "Microsoft.Lookup",
            Lookup = new LookupPayload
            {
                ConnectionName = "CM_Sql",
                SqlCommand = "SELECT CustomerID FROM dbo.SyntheticCustomer",
                CacheTypeRaw = 1,
                ReferenceColumns = [new LookupReferenceColumnSpec { Name = "CustomerID", DataType = "DT_I4" }],
            },
        };

        var result = LookupCacheEmitter.Emit("Generated.Mapping", "LookupCache", partialCacheLookup);

        Assert.Empty(result.Files);
        var gap = Assert.Single(result.Gaps);
        Assert.Contains("only full cache (0) is supported yet", gap.Reason);
    }
}
