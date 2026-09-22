using Ssis.Extract.Dtsx;

namespace Ssis.Extract.Tests;

/// <summary>
/// Covers the Phase 1 fix (see the plan at concurrent-whistling-turing.md and CLAUDE.md's own
/// "Tools reorganized" history): a real SSIS Project-Deployment-Model project routinely stores
/// its connection managers as separate, project-level <c>.conmgr</c> files referenced by name
/// from the <c>.dtproj</c> manifest's own <c>&lt;SSIS:ConnectionManagers&gt;</c> list, rather
/// than embedding them inside every <c>.dtsx</c> -- confirmed byte-for-byte the same XML schema
/// as a package-embedded <c>&lt;DTS:ConnectionManager&gt;</c> against real GitHub SSIS
/// portfolios (e.g. <c>WWI_Source_DB.conmgr</c> in <c>sql-server-samples</c>'s <c>wwi-ssis</c>).
/// Built as real filesystem tests (a temp project directory with a real <c>.dtproj</c> +
/// <c>.conmgr</c> pair) rather than XElement-only unit tests, since the whole point of this fix
/// is the FILE RESOLUTION (name -> path relative to the .dtproj's own directory), not just the
/// XML parsing (which <c>DtsxPackageReader.ReadConnectionManager</c> already covered).
/// </summary>
public class DtprojReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ssisx-dtproj-tests-").FullName;

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

    private static string DtprojXml(string conmgrFileName) => $$"""
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
                  <SSIS:ConnectionManager SSIS:Name="{{conmgrFileName}}" />
                </SSIS:ConnectionManagers>
                <SSIS:DeploymentInfo>
                  <SSIS:PackageInfo />
                </SSIS:DeploymentInfo>
              </SSIS:Project>
            </Manifest>
          </DeploymentModelSpecificContent>
        </Project>
        """;

    private string WriteProject(string conmgrFileName)
    {
        File.WriteAllText(Path.Combine(_root, conmgrFileName), ConmgrXml);
        var dtprojPath = Path.Combine(_root, "Test.dtproj");
        File.WriteAllText(dtprojPath, DtprojXml(conmgrFileName));
        return dtprojPath;
    }

    [Fact]
    public void Read_ResolvesAProjectScopedConnectionManager_FromAStandaloneConmgrFile()
    {
        var dtprojPath = WriteProject("CM_Test.conmgr");

        var project = DtprojReader.Read(dtprojPath, projectParamsPath: null);

        var cm = Assert.Single(project.ConnectionManagers);
        Assert.Equal("CM_Test", cm.ObjectName);
        Assert.Equal("Project", cm.Scope);
        Assert.Equal("Project.ConnectionManagers[CM_Test]", cm.RefId);
        Assert.Equal("{11111111-1111-1111-1111-111111111111}", cm.DtsId);
        Assert.Equal("OLEDB", cm.CreationName);
        Assert.NotNull(cm.Parsed);
        Assert.Equal("TestDb", cm.Parsed!.Database);
    }

    [Fact]
    public void Read_UsesTheConnectionManagersOwnObjectName_NotTheFileName_WhenTheyDiffer()
    {
        // A real portfolio's .conmgr filename usually matches its own ObjectName, but nothing
        // guarantees it -- the refId shape a .dtsx's pipeline/task XML actually references
        // ("Project.ConnectionManagers[{Name}]") is keyed by ObjectName, confirmed real, so
        // that's what must be used here too, not a name derived from the file on disk.
        var dtprojPath = WriteProject("SomeOtherFileName.conmgr");

        var project = DtprojReader.Read(dtprojPath, projectParamsPath: null);

        var cm = Assert.Single(project.ConnectionManagers);
        Assert.Equal("CM_Test", cm.ObjectName);
        Assert.Equal("Project.ConnectionManagers[CM_Test]", cm.RefId);
    }

    [Fact]
    public void Read_SkipsAMissingConmgrFile_RatherThanThrowing()
    {
        // The manifest references a .conmgr that was never actually checked in (or was
        // filtered out of a partial checkout) -- must not crash the whole project read; the
        // resulting unresolved connection reference still surfaces downstream as its own gap.
        var dtprojPath = Path.Combine(_root, "Test.dtproj");
        File.WriteAllText(dtprojPath, DtprojXml("DoesNotExist.conmgr"));

        var project = DtprojReader.Read(dtprojPath, projectParamsPath: null);

        Assert.Empty(project.ConnectionManagers);
    }

    [Fact]
    public void Read_RedactsThePasswordEvenForAProjectScopedConnectionManager_UnlessNoRedactIsSet()
    {
        var conmgrWithPassword = ConmgrXml.Replace(
            "Integrated Security=SSPI;",
            "User ID=sa;Password=hunter2;");
        File.WriteAllText(Path.Combine(_root, "CM_Test.conmgr"), conmgrWithPassword);
        var dtprojPath = Path.Combine(_root, "Test.dtproj");
        File.WriteAllText(dtprojPath, DtprojXml("CM_Test.conmgr"));

        var redacted = DtprojReader.Read(dtprojPath, projectParamsPath: null, noRedact: false);
        var unredacted = DtprojReader.Read(dtprojPath, projectParamsPath: null, noRedact: true);

        Assert.DoesNotContain("hunter2", redacted.ConnectionManagers[0].ConnectionString);
        Assert.True(redacted.ConnectionManagers[0].WasRedacted);
        Assert.Null(redacted.ConnectionManagers[0].UnredactedConnectionString);
        Assert.Contains("hunter2", unredacted.ConnectionManagers[0].UnredactedConnectionString);
    }
}
