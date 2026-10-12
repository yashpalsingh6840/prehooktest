using Ssis.Extract.Dtsx;

namespace Ssis.Extract.Tests;

/// <summary>
/// Phase 2.4 (C3) of the independent-review fix plan: <c>DTS:Configurations</c> -- a legacy
/// Package-Deployment-Model configuration override -- is now read into
/// <see cref="Ssis.Extract.Model.Package.PackageSpec.Configurations"/> instead of falling
/// unmodeled into <c>Unmapped</c>. The real evidenced shape (attribute names/values) is copied
/// verbatim from RBC_Demo_ETL's own <c>Package_Legacy.dtsx</c>.
/// </summary>
public class PackageConfigurationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ssisx-package-config-tests-").FullName;

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
          <DTS:Configurations>
            <DTS:Configuration
              DTS:ConfigurationString="D:\OneDrive\config\Package_Legacy.dtsConfig"
              DTS:ConfigurationType="1"
              DTS:CreationName=""
              DTS:DTSID="{4C6DBCAC-B91E-4412-98DD-512F52E4F2B7}"
              DTS:ObjectName="LegacyXmlConfig" />
          </DTS:Configurations>
          <DTS:Executables />
        </DTS:Executable>
        """;

    private string WriteDtsx()
    {
        var path = Path.Combine(_root, "TestPkg.dtsx");
        File.WriteAllText(path, DtsxXml);
        return path;
    }

    [Fact]
    public void Read_PopulatesConfigurations_FromTheRealEvidencedShape()
    {
        var path = WriteDtsx();

        var package = DtsxPackageReader.Read(path, noRedact: false);

        var config = Assert.Single(package.Configurations);
        Assert.Equal("LegacyXmlConfig", config.ObjectName);
        Assert.Equal("1", config.ConfigurationType);
        Assert.Equal(@"D:\OneDrive\config\Package_Legacy.dtsConfig", config.ConfigurationString);
        Assert.Null(config.ConnectionString);

        // Now that it's modeled, it must not ALSO appear as an unmapped fragment -- the whole
        // point of wiring the reader was to move this out of Unmapped, not duplicate it.
        Assert.DoesNotContain(package.Unmapped, u => u.Location == "Package/Configurations");
    }

    [Fact]
    public void Read_LeavesConfigurationsEmpty_WhenThePackageDeclaresNone()
    {
        const string xml = """
            <?xml version="1.0"?>
            <DTS:Executable xmlns:DTS="www.microsoft.com/SqlServer/Dts"
              DTS:refId="Package"
              DTS:ObjectName="TestPkg"
              DTS:ExecutableType="Microsoft.Package">
              <DTS:ConnectionManagers />
              <DTS:Executables />
            </DTS:Executable>
            """;
        var path = Path.Combine(_root, "NoConfig.dtsx");
        File.WriteAllText(path, xml);

        var package = DtsxPackageReader.Read(path, noRedact: false);

        Assert.Empty(package.Configurations);
    }
}
