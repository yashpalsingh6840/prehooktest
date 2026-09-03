using System.Xml.Linq;
using Ssis.Extract.Dtsx;

namespace Ssis.Extract.Tests;

/// <summary>
/// Covers <see cref="DtsxPackageReader.ReadExecutePackageTask"/> directly against hand-built XML
/// matching the real, empirically-confirmed shape (see <c>ExecutePackageTaskPayload</c>'s own
/// doc comment for the live object-model round trip this was verified against) -- no PoC package
/// has an Execute Package Task, so a hand-built element is the only way to exercise this reader
/// at all, the same reasoning already used for the DPAPI-ciphertext connection manager tests.
/// </summary>
public class ExecutePackageTaskTests
{
    [Fact]
    public void ReadExecutePackageTask_ReadsProjectReferenceMode()
    {
        var objectData = new XElement("ObjectData",
            new XElement("ExecutePackageTask",
                new XElement("UseProjectReference", "True"),
                new XElement("PackageName", "ChildInSameProject.dtsx")));

        var payload = DtsxPackageReader.ReadExecutePackageTask(objectData, [], new DtsxPackageReader.CoverageAccumulator());

        Assert.True(payload.UseProjectReference);
        Assert.Equal("ChildInSameProject.dtsx", payload.PackageName);
        Assert.Null(payload.ConnectionRefRaw);
        Assert.Null(payload.ConnectionName);
        Assert.Null(payload.ExecuteOutOfProcess);
    }

    [Fact]
    public void ReadExecutePackageTask_ReadsFileReferenceMode_AndResolvesTheConnection()
    {
        var objectData = new XElement("ObjectData",
            new XElement("ExecutePackageTask",
                new XElement("ExecuteOutOfProcess", "True"),
                new XElement("PackageName", "ChildPackage.dtsx"),
                new XElement("Connection", "{D1B927D4-C0DF-4F42-BB80-ACFE550435D4}")));
        var cmDtsIdToName = new Dictionary<string, string> { ["{D1B927D4-C0DF-4F42-BB80-ACFE550435D4}"] = "CM_FILE_ChildPackage" };

        var payload = DtsxPackageReader.ReadExecutePackageTask(objectData, cmDtsIdToName, new DtsxPackageReader.CoverageAccumulator());

        Assert.True(payload.ExecuteOutOfProcess);
        Assert.Null(payload.UseProjectReference);
        Assert.Equal("ChildPackage.dtsx", payload.PackageName);
        Assert.Equal("{D1B927D4-C0DF-4F42-BB80-ACFE550435D4}", payload.ConnectionRefRaw);
        Assert.Equal("CM_FILE_ChildPackage", payload.ConnectionName);
    }

    [Fact]
    public void ReadExecutePackageTask_LeavesConnectionNameNull_WhenTheReferenceIsDangling()
    {
        var objectData = new XElement("ObjectData",
            new XElement("ExecutePackageTask",
                new XElement("Connection", "{00000000-0000-0000-0000-000000000000}")));

        var payload = DtsxPackageReader.ReadExecutePackageTask(objectData, [], new DtsxPackageReader.CoverageAccumulator());

        Assert.Equal("{00000000-0000-0000-0000-000000000000}", payload.ConnectionRefRaw);
        Assert.Null(payload.ConnectionName);
    }

    [Fact]
    public void ReadExecutePackageTask_BumpsCoverageUnmapped_ForParameterAssignments_RatherThanSilentlyDroppingThem()
    {
        var objectData = new XElement("ObjectData",
            new XElement("ExecutePackageTask",
                new XElement("UseProjectReference", "True"),
                new XElement("PackageName", "Child.dtsx"),
                new XElement("ParameterAssignments",
                    new XElement("ParameterAssignment", new XAttribute("ParameterName", "SomeParam")))));
        var coverage = new DtsxPackageReader.CoverageAccumulator();

        var payload = DtsxPackageReader.ReadExecutePackageTask(objectData, [], coverage);

        Assert.Equal("Child.dtsx", payload.PackageName);
        Assert.True(coverage.Unmapped > 0);
    }

    [Fact]
    public void ReadExecutePackageTask_ReturnsEmptyPayload_WhenTheElementIsMissing()
    {
        var objectData = new XElement("ObjectData");

        var payload = DtsxPackageReader.ReadExecutePackageTask(objectData, [], new DtsxPackageReader.CoverageAccumulator());

        Assert.Null(payload.PackageName);
        Assert.Null(payload.UseProjectReference);
        Assert.Null(payload.ConnectionName);
    }
}
