using System.Xml.Linq;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Reads <c>Project.params</c> (and the structurally-identical
/// <c>SSIS:Parameters</c>/<c>SSIS:Parameter</c> block embedded in a .dtproj's package
/// manifest -- see <see cref="DtprojReader"/>) into <see cref="SsisParameter"/>. The
/// DataType code here is a .NET <see cref="TypeCode"/> ordinal (CLAUDE.md trap 12),
/// resolved via <see cref="SsisTypeCodeMaps.ProjectParamsDataTypeName"/> -- never the
/// pipeline or variant tables used elsewhere.
/// </summary>
public static class ProjectParamsReader
{
    private static readonly XNamespace Ssis = "www.microsoft.com/SqlServer/SSIS";

    public static List<SsisParameter> Read(string path, string scope)
    {
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        return ReadFromElement(doc.Root!, scope);
    }

    /// <summary>Parses a <c>&lt;SSIS:Parameters&gt;</c> element already in memory (used by <see cref="DtprojReader"/> for package-scope parameters embedded in the .dtproj manifest).</summary>
    public static List<SsisParameter> ReadFromElement(XElement parametersEl, string scope)
    {
        var result = new List<SsisParameter>();
        foreach (var paramEl in parametersEl.Elements(Ssis + "Parameter"))
        {
            var name = paramEl.Attr(Ssis + "Name") ?? throw new InvalidDataException("SSIS:Parameter missing SSIS:Name");
            var props = paramEl.Element(Ssis + "Properties");

            string? Get(string propName) => props?.Elements(Ssis + "Property")
                .FirstOrDefault(p => p.Attr(Ssis + "Name") == propName)?.Value;

            var dataTypeRaw = int.TryParse(Get("DataType"), out var dt) ? dt : 0;

            result.Add(new SsisParameter
            {
                Name = name,
                Id = Get("ID"),
                CreationName = NullIfEmpty(Get("CreationName")),
                Description = NullIfEmpty(Get("Description")),
                IncludeInDebugDump = int.TryParse(Get("IncludeInDebugDump"), out var idd) ? idd : null,
                Required = Get("Required") == "1",
                Sensitive = Get("Sensitive") == "1",
                Value = Get("Value"),
                DataTypeRaw = dataTypeRaw,
                DataTypeName = SsisTypeCodeMaps.ProjectParamsDataTypeName(dataTypeRaw),
                Scope = scope,
            });
        }
        return result;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
