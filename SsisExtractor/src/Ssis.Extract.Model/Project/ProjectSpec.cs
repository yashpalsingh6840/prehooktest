using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Model.Project;

/// <summary>
/// The project-level spec (plan §4.1) -- what <c>project.spec.json</c> serializes. Read
/// from <c>Project.params</c> (project parameters), <c>.dtproj</c> (deployment manifest:
/// package list, build configurations), and <c>SSIS.database</c> is intentionally not
/// read (DataTools artifact, not part of the deployed project).
/// </summary>
public sealed class ProjectSpec
{
    public required string DeploymentModel { get; init; }
    public required string ProductVersion { get; init; }
    public required string SchemaVersion { get; init; }

    public int? ProtectionLevelRaw { get; init; }
    public string? ProtectionLevelName { get; init; }

    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? VersionMajor { get; init; }
    public string? VersionMinor { get; init; }
    public string? VersionBuild { get; init; }
    public string? VersionComments { get; init; }
    public string? CreationDate { get; init; }
    public string? CreatorName { get; init; }
    public string? CreatorComputerName { get; init; }
    public string? Description { get; init; }
    public string? FormatVersion { get; init; }

    public List<PackageManifestEntry> Packages { get; init; } = [];

    /// <summary>Project-scoped connection managers -- empty in this PoC, common in the wild (plan §4.1).</summary>
    public List<ConnectionManagerSpec> ConnectionManagers { get; init; } = [];

    /// <summary>Project-scoped parameters, from <c>Project.params</c>.</summary>
    public List<SsisParameter> Parameters { get; init; } = [];

    public List<BuildConfigurationSpec> BuildConfigurations { get; init; } = [];

    public required string SourceDtprojPath { get; init; }
    public string? SourceProjectParamsPath { get; init; }
}

/// <summary>
/// One package as listed in the .dtproj deployment manifest. Comparing this against the
/// package's own on-disk identity (<see cref="Package.PackageSpec.VersionGuid"/> etc.) is
/// how source/deployed drift is detected without opening anything (plan §4.1).
/// </summary>
public sealed class PackageManifestEntry
{
    public required string Name { get; init; }
    public bool EntryPoint { get; init; }
    public string? VersionGuid { get; init; }
    public int? PackageFormatVersion { get; init; }
    public int? ProtectionLevelRaw { get; init; }
    public string? ProtectionLevelName { get; init; }
    public string? Description { get; init; }

    /// <summary>Package-scoped parameters as declared in the manifest (mirrors what's in the .dtsx itself).</summary>
    public List<SsisParameter> Parameters { get; init; } = [];
}

public sealed class BuildConfigurationSpec
{
    public required string Name { get; init; }
    public string? OutputPath { get; init; }
    public string? TargetServerVersion { get; init; }
}
