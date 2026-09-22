namespace Ssis.Extract.Cli.Tests;

/// <summary>
/// End-to-end proof of the Phase 1 fix (see the plan at
/// C:\Users\yashpal.singh01\.claude\plans\concurrent-whistling-turing.md and CLAUDE.md's own
/// "Tools reorganized" history): a real <c>.dtproj</c> + standalone <c>.conmgr</c> file +
/// <c>.dtsx</c> referencing a project-scoped connection manager, loaded through
/// <see cref="PackageLoader.Load"/> exactly the way <c>ssisx extract</c>/<c>generate</c> do --
/// not the two lower-level pieces in isolation (<c>DtprojReaderTests</c>/
/// <c>DtsxPackageReaderProjectConnectionManagerTests</c> in <c>Ssis.Extract.Tests</c> already
/// cover those). This is the actual real-world path that was broken: before this fix, a
/// project-scoped pipeline connection resolved to null regardless of how correctly the .conmgr
/// file itself parsed, because nothing ever threaded the resolved project connection managers
/// from <c>LoadProject</c> into <c>LoadDtsx</c>.
/// </summary>
public class PackageLoaderProjectConnectionManagerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ssisx-packageloader-project-cm-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string ConmgrXml = """
        <?xml version="1.0"?>
        <DTS:ConnectionManager xmlns:DTS="www.microsoft.com/SqlServer/Dts"
          DTS:ObjectName="CM_Test"
          DTS:DTSID="{11111111-1111-1111-1111-111111111111}"
          DTS:CreationName="OLEDB">
          <DTS:ObjectData>
            <DTS:ConnectionManager
              DTS:ConnectionString="Data Source=.;Initial Catalog=TestDb;Provider=SQLNCLI11.1;Integrated Security=SSPI;" />
          </DTS:ObjectData>
        </DTS:ConnectionManager>
        """;

    private const string DtprojXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <DeploymentModel>Project</DeploymentModel>
          <ProductVersion>17.0.0.0</ProductVersion>
          <SchemaVersion>3</SchemaVersion>
          <DeploymentModelSpecificContent>
            <Manifest>
              <SSIS:Project xmlns:SSIS="www.microsoft.com/SqlServer/SSIS" SSIS:ProtectionLevel="DontSaveSensitive">
                <SSIS:Properties>
                  <SSIS:Property SSIS:Name="ID">{22222222-2222-2222-2222-222222222222}</SSIS:Property>
                  <SSIS:Property SSIS:Name="Name">TestProject</SSIS:Property>
                </SSIS:Properties>
                <SSIS:Packages>
                  <SSIS:Package SSIS:Name="TestPkg.dtsx" SSIS:EntryPoint="1" />
                </SSIS:Packages>
                <SSIS:ConnectionManagers>
                  <SSIS:ConnectionManager SSIS:Name="CM_Test.conmgr" />
                </SSIS:ConnectionManagers>
                <SSIS:DeploymentInfo>
                  <SSIS:PackageInfo />
                </SSIS:DeploymentInfo>
              </SSIS:Project>
            </Manifest>
          </DeploymentModelSpecificContent>
        </Project>
        """;

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

    [Fact]
    public void Load_ResolvesAPipelineSourcesProjectScopedConnectionManager_ThroughTheFullDtprojToDtsxPath()
    {
        File.WriteAllText(Path.Combine(_root, "CM_Test.conmgr"), ConmgrXml);
        File.WriteAllText(Path.Combine(_root, "Test.dtproj"), DtprojXml);
        File.WriteAllText(Path.Combine(_root, "TestPkg.dtsx"), DtsxXml);

        using var result = PackageLoader.Load(_root, noRedact: false, recursive: false);

        Assert.Empty(result.Failures);
        var project = Assert.Single(result.Projects).Project;
        var projectCm = Assert.Single(project.ConnectionManagers);
        Assert.Equal("CM_Test", projectCm.ObjectName);
        Assert.Equal("Project", projectCm.Scope);

        var package = Assert.Single(result.Packages);
        var source = Assert.Single(package.Executables[0].DataFlowTask!.Pipeline.Components);
        var connection = Assert.Single(source.Connections);
        Assert.Equal("CM_Test", connection.ConnectionManagerName);

        var packageCm = Assert.Single(package.ConnectionManagers);
        Assert.Equal("CM_Test", packageCm.ObjectName);
        Assert.Equal("Project", packageCm.Scope);
    }
}
