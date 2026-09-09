using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Tests;

/// <summary>
/// <see cref="PipelineResolver"/> + <see cref="SsisPipelineTypeMap"/> -- the two pieces a code
/// generator needs before it can emit a single property declaration.
///
/// <para>These assert against the <b>real</b> PoC packages rather than a hand-built spec, and
/// the expected values are not invented: they are exactly what the hand-written C# rewrite at
/// <c>D:\PoC\SSIS_Migration</c> declares in <c>EmployeeDbContext.OnModelCreating</c>
/// (<c>HasMaxLength(20)</c> on EmployeeKey, <c>decimal(18,2)</c> on Salary, <c>date</c> on
/// HireDate, <c>datetime2(3)</c> on LoadedAtUtc). That rewrite already passes gate 3 against a
/// real database, so agreeing with it is evidence the resolver recovers the true target schema
/// -- not merely that it runs. If a generator ever disagrees with these, the generator is
/// wrong.</para>
/// </summary>
public class PipelineResolverTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    // tests/Ssis.Extract.Tests -> Tools/SsisExtractor -> repo root -> SSIS/
    private static readonly string PoCPackagesDir = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(ThisFilePath())!, "..", "..", "..", "..", "..", "SSIS_Packages", "SSIS"));

    private static ResolvedColumnSet ResolveDestination(string dtsxFileName, string componentName)
    {
        var package = DtsxPackageReader.Read(Path.Combine(PoCPackagesDir, dtsxFileName), noRedact: false);
        var component = PackageTree.AllExecutables(package)
            .Where(e => e.DataFlowTask?.Pipeline is not null)
            .SelectMany(e => e.DataFlowTask!.Pipeline!.Components)
            .Single(c => c.Name == componentName);
        return PipelineResolver.ResolveDestinationInput(component);
    }

    private static ResolvedColumn Column(ResolvedColumnSet set, string externalName) =>
        set.Columns.Single(c => c.ExternalColumnName == externalName);

    [Fact]
    public void LoadEmployees_RecoversEveryDestinationColumn_WithNothingUnresolved()
    {
        var resolved = ResolveDestination("LoadEmployees.dtsx", "OLEDST_Employee");

        Assert.Empty(resolved.Unresolved);
        Assert.Equal(
            ["Department", "EmployeeID", "EmployeeKey", "FullName", "HireDate", "LoadedAtUtc", "Location", "Salary"],
            resolved.Columns.Select(c => c.ExternalColumnName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void LoadEmployees_RecoversTheFacetsThatPipelineColumnMappingSpecDiscards()
    {
        // The whole reason PipelineResolver exists: these four values are absent from
        // PipelineColumnMappingSpec, and without them no EF model can be emitted.
        var resolved = ResolveDestination("LoadEmployees.dtsx", "OLEDST_Employee");

        Assert.Equal(20, Column(resolved, "EmployeeKey").Length);
        Assert.Equal(101, Column(resolved, "FullName").Length);
        Assert.Equal(60, Column(resolved, "Location").Length);

        var salary = Column(resolved, "Salary");
        Assert.Equal(18, salary.Precision);
        Assert.Equal(2, salary.Scale);

        Assert.Equal(3, Column(resolved, "LoadedAtUtc").Scale);
    }

    [Theory]
    // Expected values lifted from the hand-written EmployeeDbContext, not invented here.
    [InlineData("EmployeeID", "int", null)]
    [InlineData("EmployeeKey", "string", null)]
    [InlineData("FullName", "string", null)]
    [InlineData("HireDate", "DateOnly", "date")]
    [InlineData("Salary", "decimal", "decimal(18,2)")]
    [InlineData("LoadedAtUtc", "DateTime", "datetime2(3)")]
    public void LoadEmployees_MapsEachColumnToTheClrTypeAndSqlFacetTheRewriteUses(
        string externalColumnName, string expectedClrType, string? expectedColumnType)
    {
        var column = Column(ResolveDestination("LoadEmployees.dtsx", "OLEDST_Employee"), externalColumnName);

        Assert.NotNull(column.Type);
        Assert.Equal(expectedClrType, column.Type!.ClrTypeName);
        Assert.Equal(expectedColumnType,
            SsisPipelineTypeMap.RenderColumnType(column.Type, column.Length, column.Precision, column.Scale));
    }

    [Fact]
    public void LoadReferenceData_ResolvesBothDestinations_NothingUnresolved()
    {
        foreach (var componentName in new[] { "OLEDST_Department", "OLEDST_Designation" })
        {
            var resolved = ResolveDestination("LoadReferenceData.dtsx", componentName);
            Assert.Empty(resolved.Unresolved);
            Assert.NotEmpty(resolved.Columns);
            Assert.All(resolved.Columns, c => Assert.NotNull(c.Type));
        }
    }

    [Fact]
    public void UnknownPipelineType_ResolvesToNull_RatherThanADefault()
    {
        // The load-bearing property for a generator: a wrong CLR type compiles cleanly and
        // corrupts data, so an unrecognised type must stop that column, not become `string`.
        Assert.Null(SsisPipelineTypeMap.Resolve("dt_something_from_a_future_ssis"));
        Assert.Null(SsisPipelineTypeMap.Resolve(null));
    }

    [Fact]
    public void PipelineTypeLookupIsCaseInsensitive()
    {
        // Real packages spell it "dbTimeStamp2" -- mixed case, unlike every other code.
        Assert.Equal("DT_DBTIMESTAMP2", SsisPipelineTypeMap.Resolve("dbTimeStamp2")!.DtName);
        Assert.Equal("DT_DBTIMESTAMP2", SsisPipelineTypeMap.Resolve("DBTIMESTAMP2")!.DtName);
    }

    [Fact]
    public void RenderColumnType_ReturnsNull_WhenTheColumnLacksAFacetTheTemplateNeeds()
    {
        // A decimal(0,0) would be worse than no code at all -- the caller must report the gap.
        var numeric = SsisPipelineTypeMap.Resolve("numeric")!;
        Assert.Null(SsisPipelineTypeMap.RenderColumnType(numeric, length: null, precision: null, scale: 2));
        Assert.Equal("decimal(18,2)", SsisPipelineTypeMap.RenderColumnType(numeric, null, 18, 2));
    }

    [Fact]
    public void ColumnWithNoExternalMetadataReference_IsReported_NotSilentlyDropped()
    {
        // PipelineReader.BuildColumnMappings hits `continue` here, so the mapping list can be
        // shorter than the column list with no signal. This is the behaviour that replaces it.
        var input = new PipelineInputSpec
        {
            RefId = "Package\\DFT\\Dest.Inputs[Destination Input]",
            Name = "Destination Input",
            Columns =
            [
                new PipelineInputColumnSpec
                {
                    RefId = "Package\\DFT\\Dest.Inputs[Destination Input].Columns[Orphan]",
                    CachedName = "Orphan",
                    LineageId = "whatever",
                    ExternalMetadataColumnId = null,
                },
                new PipelineInputColumnSpec
                {
                    RefId = "Package\\DFT\\Dest.Inputs[Destination Input].Columns[Dangling]",
                    CachedName = "Dangling",
                    LineageId = "whatever",
                    ExternalMetadataColumnId = "points-at-nothing",
                },
            ],
        };

        var resolved = PipelineResolver.Resolve(input);

        Assert.Empty(resolved.Columns);
        Assert.Equal(2, resolved.Unresolved.Count);
        Assert.Contains(resolved.Unresolved, u => u.ColumnName == "Orphan" && u.Reason.Contains("no ExternalMetadataColumnId"));
        Assert.Contains(resolved.Unresolved, u => u.ColumnName == "Dangling" && u.Reason.Contains("points-at-nothing"));
    }
}
