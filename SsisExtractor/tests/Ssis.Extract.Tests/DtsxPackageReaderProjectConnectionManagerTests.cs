using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Tests;

/// <summary>
/// Covers the other half of the Phase 1 fix (see <see cref="DtprojReaderTests"/> for the
/// .conmgr-file-resolution half): once <see cref="DtprojReader"/> has resolved a project-scoped
/// connection manager, <see cref="DtsxPackageReader.Read(string, bool, IReadOnlyList{ConnectionManagerSpec}?)"/>
/// must (a) resolve a pipeline component's own
/// <c>connectionManagerRefId="Project.ConnectionManagers[{Name}]"</c> reference to that CM's real
/// name -- exactly the reference shape confirmed real against <c>DailyETLMain.dtsx</c> -- and
/// (b) fold the project CM into the package's own returned <c>ConnectionManagers</c> list, still
/// tagged <c>Scope: "Project"</c>, so every existing downstream consumer
/// (<c>PackagePlanner</c>/<c>PackageGenerator</c>'s name-based lookups) keeps working unchanged.
/// </summary>
public class DtsxPackageReaderProjectConnectionManagerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ssisx-project-cm-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string DtsxXml = """
        <?xml version="1.0"?>
        <DTS:Executable xmlns:DTS="www.microsoft.com/SqlServer/Dts"
          DTS:refId="Package"
          DTS:ObjectName="TestPkg"
          DTS:ExecutableType="Microsoft.Package">
          <DTS:ConnectionManagers />
          <DTS:Executables>
            <DTS:Executable
              DTS:refId="Package\DFT_Test"
              DTS:ObjectName="DFT_Test"
              DTS:ExecutableType="Microsoft.Pipeline">
              <DTS:ObjectData>
                <pipeline version="1">
                  <components>
                    <component refId="Package\DFT_Test\OLE DB Source" name="OLE DB Source" componentClassID="Microsoft.OLEDBSource">
                      <connections>
                        <connection refId="Package\DFT_Test\OLE DB Source.Connections[OleDbConnection]" name="OleDbConnection" connectionManagerRefId="Project.ConnectionManagers[CM_Test]" />
                      </connections>
                    </component>
                  </components>
                </pipeline>
              </DTS:ObjectData>
            </DTS:Executable>
          </DTS:Executables>
        </DTS:Executable>
        """;

    private string WriteDtsx()
    {
        var path = Path.Combine(_root, "TestPkg.dtsx");
        File.WriteAllText(path, DtsxXml);
        return path;
    }

    private static ConnectionManagerSpec ProjectCm() => new()
    {
        ObjectName = "CM_Test",
        RefId = "Project.ConnectionManagers[CM_Test]",
        DtsId = "{11111111-1111-1111-1111-111111111111}",
        CreationName = "OLEDB",
        Scope = "Project",
        ConnectionString = "Data Source=.;Initial Catalog=TestDb;",
        WasRedacted = false,
    };

    [Fact]
    public void Read_ResolvesAPipelineConnectionAgainstAProjectScopedConnectionManager()
    {
        var path = WriteDtsx();

        var package = DtsxPackageReader.Read(path, noRedact: false, projectConnectionManagers: [ProjectCm()]);

        var flow = Assert.Single(package.Executables);
        var source = Assert.Single(flow.DataFlowTask!.Pipeline.Components);
        var connection = Assert.Single(source.Connections);
        Assert.Equal("Project.ConnectionManagers[CM_Test]", connection.ConnectionManagerRefRaw);
        Assert.Equal("CM_Test", connection.ConnectionManagerName);
    }

    [Fact]
    public void Read_LeavesAProjectScopedConnectionUnresolved_WhenNoProjectConnectionManagersAreSupplied()
    {
        // The regression this whole phase closes: before the fix, this is exactly what every
        // real package in the affected GitHub portfolios produced -- a null connection manager
        // name, which is what "Source has no resolvable connection manager" is built from.
        var path = WriteDtsx();

        var package = DtsxPackageReader.Read(path, noRedact: false);

        var source = Assert.Single(package.Executables[0].DataFlowTask!.Pipeline.Components);
        var connection = Assert.Single(source.Connections);
        Assert.Null(connection.ConnectionManagerName);
    }

    [Fact]
    public void Read_FoldsTheProjectConnectionManagerIntoThePackagesOwnConnectionManagersList()
    {
        var path = WriteDtsx();

        var package = DtsxPackageReader.Read(path, noRedact: false, projectConnectionManagers: [ProjectCm()]);

        var cm = Assert.Single(package.ConnectionManagers);
        Assert.Equal("CM_Test", cm.ObjectName);
        Assert.Equal("Project", cm.Scope);
    }
}
