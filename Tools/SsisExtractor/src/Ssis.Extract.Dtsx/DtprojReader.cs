using System.Xml.Linq;
using Ssis.Extract.Model.Project;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Reads a <c>.dtproj</c> file: the top-level project attributes (no namespace -- the
/// project's root element declares none), plus the SSIS-namespaced deployment manifest
/// nested inside <c>DeploymentModelSpecificContent/Manifest</c>, which carries the package
/// list, project GUID/version/provenance, and per-package metadata including package-scope
/// parameter declarations (redundant with, but a useful drift-check against, what's
/// actually in the .dtsx -- see plan §4.1 "detect source/deployed drift").
/// </summary>
public static class DtprojReader
{
    private static readonly XNamespace Ssis = "www.microsoft.com/SqlServer/SSIS";

    // Re-declared rather than shared with DtsxPackageReader's own identical constant -- a
    // standalone .conmgr file is DTS-namespaced XML, read here for the one attribute
    // (ObjectName) needed to synthesize its "Project.ConnectionManagers[{Name}]" refId
    // before handing the whole element to DtsxPackageReader.ReadConnectionManager.
    private static readonly XNamespace Dts = "www.microsoft.com/SqlServer/Dts";

    public static ProjectSpec Read(string dtprojPath, string? projectParamsPath, bool noRedact = false)
    {
        var doc = XDocument.Load(dtprojPath, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidDataException($"{dtprojPath}: no root element");

        string? TopLevel(string name) => root.Element(name)?.Value;

        var manifestProject = root
            .Element("DeploymentModelSpecificContent")?
            .Element("Manifest")?
            .Element(Ssis + "Project");

        var manifestProps = manifestProject?.Element(Ssis + "Properties");
        string? ManifestProp(string name) => manifestProps?.Elements(Ssis + "Property")
            .FirstOrDefault(p => p.Attr(Ssis + "Name") == name)?.Value;

        var protectionLevelRaw = manifestProject?.Attr(Ssis + "ProtectionLevel");
        // The manifest's own ProtectionLevel is textual (e.g. "DontSaveSensitive"), unlike
        // the numeric form used everywhere else (.dtsx ProtectionLevel, Project element).
        // Keep the raw text as the "name" and leave the numeric field null rather than
        // guess a reverse mapping.

        var parameters = projectParamsPath is not null && File.Exists(projectParamsPath)
            ? ProjectParamsReader.Read(projectParamsPath, scope: "Project")
            : [];

        var packages = new List<PackageManifestEntry>();
        var packageMetaByName = manifestProject?
            .Element(Ssis + "DeploymentInfo")?
            .Element(Ssis + "PackageInfo")?
            .Elements(Ssis + "PackageMetaData")
            .ToDictionary(el => el.Attr(Ssis + "Name") ?? "", el => el)
            ?? [];

        foreach (var pkgEl in manifestProject?.Element(Ssis + "Packages")?.Elements(Ssis + "Package") ?? [])
        {
            var pkgName = pkgEl.Attr(Ssis + "Name") ?? throw new InvalidDataException($"{dtprojPath}: SSIS:Package missing SSIS:Name");
            packageMetaByName.TryGetValue(pkgName, out var meta);
            var metaProps = meta?.Element(Ssis + "Properties");
            string? MetaProp(string name) => metaProps?.Elements(Ssis + "Property")
                .FirstOrDefault(p => p.Attr(Ssis + "Name") == name)?.Value;

            var protRaw = int.TryParse(MetaProp("ProtectionLevel"), out var pr) ? pr : (int?)null;

            var pkgParamsEl = meta?.Element(Ssis + "Parameters");
            var pkgParams = pkgParamsEl is not null
                ? ProjectParamsReader.ReadFromElement(pkgParamsEl, scope: "Package")
                : [];

            packages.Add(new PackageManifestEntry
            {
                Name = pkgName,
                EntryPoint = pkgEl.AttrBoolOrFalse(Ssis + "EntryPoint"),
                VersionGuid = MetaProp("VersionGUID"),
                PackageFormatVersion = int.TryParse(MetaProp("PackageFormatVersion"), out var pfv) ? pfv : null,
                ProtectionLevelRaw = protRaw,
                ProtectionLevelName = protRaw.HasValue ? SsisTypeCodeMaps.ProtectionLevelName(protRaw.Value) : null,
                Description = MetaProp("Description"),
                Parameters = pkgParams,
            });
        }

        // Project-scoped connection managers: real SSIS Project-Deployment-Model projects
        // routinely store these as separate, standalone .conmgr files referenced by name from
        // the manifest's own <SSIS:ConnectionManagers> list, rather than embedding them inside
        // every .dtsx that uses one. Confirmed byte-for-byte the same XML schema as a
        // package-embedded <DTS:ConnectionManager> (same namespace/attributes/nested
        // <DTS:ObjectData><DTS:ConnectionManager .../> shape) against real GitHub SSIS
        // portfolios -- this is a wiring gap, not a new format to parse.
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(dtprojPath)) ?? "";
        var connectionManagers = new List<ConnectionManagerSpec>();
        foreach (var cmRefEl in manifestProject?.Element(Ssis + "ConnectionManagers")?.Elements(Ssis + "ConnectionManager") ?? [])
        {
            var cmFileName = cmRefEl.Attr(Ssis + "Name");
            if (cmFileName is null) continue;

            var cmPath = Path.Combine(projectDir, cmFileName);
            if (!File.Exists(cmPath)) continue; // reported nowhere yet -- no failure-list mechanism exists on ProjectSpec today; the resulting unresolved connection reference still surfaces as its own generation gap downstream

            var cmDoc = XDocument.Load(cmPath, LoadOptions.PreserveWhitespace);
            var cmRoot = cmDoc.Root;
            if (cmRoot is null) continue;

            // The refId shape a .dtsx's own pipeline/task XML actually uses to reference a
            // project CM (e.g. connectionManagerRefId="Project.ConnectionManagers[WWI_Source_DB]",
            // confirmed real) is keyed by the connection manager's own ObjectName, not
            // necessarily its filename -- read that one attribute directly rather than
            // deriving the name from cmFileName.
            var cmObjectName = cmRoot.Attr(Dts + "ObjectName") ?? Path.GetFileNameWithoutExtension(cmFileName);
            var refId = $"Project.ConnectionManagers[{cmObjectName}]";
            connectionManagers.Add(DtsxPackageReader.ReadConnectionManager(cmRoot, noRedact, scope: "Project", refIdOverride: refId));
        }

        var buildConfigs = new List<BuildConfigurationSpec>();
        foreach (var cfgEl in root.Element("Configurations")?.Elements("Configuration") ?? [])
        {
            var options = cfgEl.Element("Options");
            buildConfigs.Add(new BuildConfigurationSpec
            {
                Name = cfgEl.Element("Name")?.Value ?? "",
                OutputPath = options?.Element("OutputPath")?.Value,
                TargetServerVersion = options?.Element("TargetServerVersion")?.Value,
            });
        }

        return new ProjectSpec
        {
            DeploymentModel = TopLevel("DeploymentModel") ?? "",
            ProductVersion = TopLevel("ProductVersion") ?? "",
            SchemaVersion = TopLevel("SchemaVersion") ?? "",
            ProtectionLevelRaw = null,
            ProtectionLevelName = protectionLevelRaw,
            Id = ManifestProp("ID"),
            Name = ManifestProp("Name"),
            VersionMajor = ManifestProp("VersionMajor"),
            VersionMinor = ManifestProp("VersionMinor"),
            VersionBuild = ManifestProp("VersionBuild"),
            VersionComments = NullIfBlank(ManifestProp("VersionComments")),
            CreationDate = ManifestProp("CreationDate"),
            CreatorName = ManifestProp("CreatorName"),
            CreatorComputerName = ManifestProp("CreatorComputerName"),
            Description = NullIfBlank(ManifestProp("Description")),
            FormatVersion = ManifestProp("FormatVersion"),
            Packages = packages,
            ConnectionManagers = connectionManagers,
            Parameters = parameters,
            BuildConfigurations = buildConfigs,
            SourceDtprojPath = dtprojPath,
            SourceProjectParamsPath = projectParamsPath,
        };
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
