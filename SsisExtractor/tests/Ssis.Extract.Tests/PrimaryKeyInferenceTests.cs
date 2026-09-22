using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Tests;

/// <summary>
/// <see cref="PrimaryKeyInference"/> -- tested against the real PoC packages, because the
/// whole point is a naming-convention guess that either matches the actual PRIMARY KEY
/// constraints or it doesn't; asserting against a hand-built fixture would only prove the
/// heuristic echoes back what the test author already encoded. All three candidates here
/// were independently cross-checked against `.\SQLFORPOC`'s real PRIMARY KEY constraints
/// (Sql/01_CreateTarget.sql) -- exact matches, not assumed.
/// </summary>
public class PrimaryKeyInferenceTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static readonly string PoCPackagesDir = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(ThisFilePath())!, "..", "..", "..", "..", "..", "SSIS_Packages", "SSIS"));

    private static readonly string FixturesDir = Path.Combine(Path.GetDirectoryName(ThisFilePath())!, "Fixtures");

    private static List<PrimaryKeyCandidateSpec> InferFor(string dtsxPath)
    {
        var package = DtsxPackageReader.Read(dtsxPath, noRedact: false);
        var allExecutables = PackageTree.AllExecutables(package).ToList();
        return PrimaryKeyInference.Infer(package, allExecutables);
    }

    [Fact]
    public void LoadEmployees_InfersEmployeeIdAsThePrimaryKey()
    {
        var candidates = InferFor(Path.Combine(PoCPackagesDir, "LoadEmployees.dtsx"));

        var candidate = Assert.Single(candidates);
        Assert.Equal("[dbo].[Employee]", candidate.TargetTable);
        Assert.Equal("NamingConvention", candidate.Confidence);
        Assert.Equal(["EmployeeID"], candidate.Columns);
    }

    [Fact]
    public void LoadEmployees_DoesNotMistakeTheDerivedBusinessKeyForThePrimaryKey()
    {
        // EmployeeKey is a Derived Column computed from Department + EmployeeID (a string
        // concat) -- it is NOT the table's real key, and it doesn't match the naming
        // convention either. Both should keep it out of the result, but for different
        // reasons; this test pins the derived-value exclusion specifically, since a naming
        // convention alone would already reject "EmployeeKey" (it doesn't equal "EmployeeID"
        // or "ID"), so a change that accidentally started matching on suffix "Key" too would
        // otherwise slip through.
        var candidates = InferFor(Path.Combine(PoCPackagesDir, "LoadEmployees.dtsx"));

        Assert.DoesNotContain("EmployeeKey", candidates.SelectMany(c => c.Columns));
    }

    [Fact]
    public void LoadReferenceData_InfersBothTargetTablesPrimaryKeys()
    {
        var candidates = InferFor(Path.Combine(PoCPackagesDir, "LoadReferenceData.dtsx"));

        Assert.Equal(2, candidates.Count);

        var department = candidates.Single(c => c.TargetTable == "[dbo].[Department]");
        Assert.Equal(["DepartmentID"], department.Columns);
        Assert.Equal("NamingConvention", department.Confidence);

        var designation = candidates.Single(c => c.TargetTable == "[dbo].[Designation]");
        Assert.Equal(["DesignationID"], designation.Columns);
        Assert.Equal("NamingConvention", designation.Confidence);
    }

    [Fact]
    public void SyntheticLookupSplit_HandlesThreeDestinationsSharingTheSameDefaultName()
    {
        // Regression pin: this fixture's three OLE DB Destinations were all left at SSIS's
        // default component name "OLE DB Destination" (plan §11's own worked Lookup +
        // Conditional Split example never renamed them). An earlier version of this feature
        // joined destinations by (DataFlowTaskPath, Name) instead of by RefId and crashed
        // with a duplicate-key exception building conformance rules against this exact
        // fixture -- caught by ConformanceTests, not by this file originally, which is why
        // this pin exists now.
        var candidates = InferFor(Path.Combine(FixturesDir, "SyntheticLookupSplit.dtsx"));

        Assert.Equal(3, candidates.Select(c => c.DestinationComponentRefId).Distinct().Count());
    }

    [Fact]
    public void PackageWithNoDataFlowTasks_ProducesNoCandidates()
    {
        // SyntheticForEachScript.dtsx is a ForEach Loop Container + Script Task -- no Data
        // Flow Task at all, so nothing for this to infer against. Reported as an empty list,
        // not an error. (Deliberately not the real, uncommitted SSIS/ScriptTest.dtsx some
        // other doc-comments in this repo verify against read-only -- that file is untracked
        // local state, and a test hard-depending on it would break on any other checkout.)
        var candidates = InferFor(Path.Combine(FixturesDir, "SyntheticForEachScript.dtsx"));

        Assert.Empty(candidates);
    }
}
