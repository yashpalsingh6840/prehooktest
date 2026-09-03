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

    public static ProjectSpec Read(string dtprojPath, string? projectParamsPath)
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
            ConnectionManagers = [], // project-scoped CMs: empty in this PoC; .dtproj manifest's SSIS:ConnectionManagers is also empty here
            Parameters = parameters,
            BuildConfigurations = buildConfigs,
            SourceDtprojPath = dtprojPath,
            SourceProjectParamsPath = projectParamsPath,
        };
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
