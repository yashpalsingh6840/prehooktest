using System.Xml.Linq;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Parses one <c>Microsoft.Pipeline</c> task's <c>&lt;pipeline&gt;</c> element (plan §4.7)
/// into a <see cref="PipelineSpec"/>. Unlike the rest of this project's DTS-namespaced XML,
/// everything inside &lt;pipeline&gt; is unqualified (no <c>DTS:</c> prefix) -- confirmed
/// directly from both PoC packages' own markup, not assumed.
/// </summary>
internal static class PipelineReader
{
    public static PipelineSpec Read(XElement pipelineEl, Dictionary<string, string> cmRefIdToName)
    {
        var components = (pipelineEl.Element("components")?.Elements("component") ?? [])
            .Select(c => ReadComponent(c, cmRefIdToName))
            .ToList();

        var paths = (pipelineEl.Element("paths")?.Elements("path") ?? [])
            .Select(p => new PipelinePathSpec
            {
                RefId = p.Attr("refId") ?? "",
                Name = p.Attr("name"),
                StartId = p.Attr("startId") ?? "",
                EndId = p.Attr("endId") ?? "",
            })
            .ToList();

        return new PipelineSpec
        {
            Version = pipelineEl.Attr("version"),
            Components = components,
            Paths = paths,
        };
    }

    private static PipelineComponentSpec ReadComponent(XElement compEl, Dictionary<string, string> cmRefIdToName)
    {
        var componentClassId = compEl.Attr("componentClassID") ?? "";

        var properties = ReadProperties(compEl.Element("properties"));

        var connections = (compEl.Element("connections")?.Elements("connection") ?? [])
            .Select(c =>
            {
                var refRaw = c.Attr("connectionManagerRefId") ?? c.Attr("connectionManagerID");
                return new PipelineComponentConnectionSpec
                {
                    RefId = c.Attr("refId") ?? "",
                    Name = c.Attr("name") ?? "",
                    Description = c.Attr("description"),
                    ConnectionManagerRefRaw = refRaw,
                    ConnectionManagerName = refRaw is not null && cmRefIdToName.TryGetValue(refRaw, out var name) ? name : null,
                };
            })
            .ToList();

        var inputs = (compEl.Element("inputs")?.Elements("input") ?? [])
            .Select(ReadInput)
            .ToList();

        var outputs = (compEl.Element("outputs")?.Elements("output") ?? [])
            .Select(ReadOutput)
            .ToList();

        var connectionName = connections.Count == 1 ? connections[0].ConnectionManagerName : null;

        FlatFileSourcePayload? flatFileSource = null;
        OleDbDestinationPayload? oleDbDestination = null;
        OleDbSourcePayload? oleDbSource = null;
        LookupPayload? lookup = null;
        ConditionalSplitPayload? conditionalSplit = null;

        if (componentClassId == "Microsoft.FlatFileSource")
        {
            var mainOutput = outputs.FirstOrDefault(o => o.IsErrorOut != true);
            flatFileSource = new FlatFileSourcePayload
            {
                ConnectionName = connectionName,
                RetainNulls = ParseBool(GetProperty(properties, "RetainNulls")),
                FileNameColumnName = NullIfEmpty(GetProperty(properties, "FileNameColumnName")),
                ColumnMappings = mainOutput is null ? [] : BuildColumnMappings(
                    mainOutput.Columns.Select(c => (c.Name, c.ExternalMetadataColumnId)),
                    mainOutput.ExternalMetadataColumns),
            };
        }
        else if (componentClassId == "Microsoft.OLEDBDestination")
        {
            var mainInput = inputs.FirstOrDefault();
            oleDbDestination = new OleDbDestinationPayload
            {
                ConnectionName = connectionName,
                OpenRowset = NullIfEmpty(GetProperty(properties, "OpenRowset")),
                SqlCommand = NullIfEmpty(GetProperty(properties, "SqlCommand")),
                AccessMode = ParseInt(GetProperty(properties, "AccessMode")),
                FastLoadOptions = NullIfEmpty(GetProperty(properties, "FastLoadOptions")),
                FastLoadKeepIdentity = ParseBool(GetProperty(properties, "FastLoadKeepIdentity")),
                FastLoadKeepNulls = ParseBool(GetProperty(properties, "FastLoadKeepNulls")),
                FastLoadMaxInsertCommitSize = ParseInt(GetProperty(properties, "FastLoadMaxInsertCommitSize")),
                CommandTimeout = ParseInt(GetProperty(properties, "CommandTimeout")),
                ColumnMappings = mainInput is null ? [] : BuildColumnMappings(
                    mainInput.Columns.Select(c => (c.CachedName, c.ExternalMetadataColumnId)),
                    mainInput.ExternalMetadataColumns),
            };
        }
        else if (componentClassId == "Microsoft.OLEDBSource")
        {
            var mainOutput = outputs.FirstOrDefault(o => o.IsErrorOut != true);
            oleDbSource = new OleDbSourcePayload
            {
                ConnectionName = connectionName,
                OpenRowset = NullIfEmpty(GetProperty(properties, "OpenRowset")),
                SqlCommand = NullIfEmpty(GetProperty(properties, "SqlCommand")),
                AccessMode = ParseInt(GetProperty(properties, "AccessMode")),
                CommandTimeout = ParseInt(GetProperty(properties, "CommandTimeout")),
                ColumnMappings = mainOutput is null ? [] : BuildColumnMappings(
                    mainOutput.Columns.Select(c => (c.Name, c.ExternalMetadataColumnId)),
                    mainOutput.ExternalMetadataColumns),
            };
        }
        else if (componentClassId == "Microsoft.Lookup")
        {
            // SSIS's own fixed names for this component's two non-error outputs -- confirmed
            // against the synthetic fixture's own saved XML, not user-renamed the way a
            // Conditional Split case is (see ConditionalSplitPayload's doc comment for the
            // contrast).
            var matchOutput = outputs.FirstOrDefault(o => o.Name == "Lookup Match Output");
            var noMatchOutput = outputs.FirstOrDefault(o => o.Name == "Lookup No Match Output");
            lookup = new LookupPayload
            {
                ConnectionName = connectionName,
                SqlCommand = NullIfEmpty(GetProperty(properties, "SqlCommand")),
                NoMatchBehaviorRaw = ParseInt(GetProperty(properties, "NoMatchBehavior")),
                CacheTypeRaw = ParseInt(GetProperty(properties, "CacheType")),
                MatchOutputName = matchOutput?.Name,
                NoMatchOutputName = noMatchOutput?.Name,
                ReferenceColumns = ParseLookupReferenceColumns(GetProperty(properties, "ReferenceMetadataXml")),
            };
        }
        else if (componentClassId == "Microsoft.ConditionalSplit")
        {
            var defaultOutput = outputs.FirstOrDefault(o =>
                o.IsErrorOut != true && ParseBool(GetProperty(o.Properties, "IsDefaultOut")) == true);
            var cases = outputs
                .Where(o => o.IsErrorOut != true && o != defaultOutput)
                .Select(o => new ConditionalSplitCaseSpec
                {
                    OutputName = o.Name,
                    Expression = GetProperty(o.Properties, "Expression"),
                    FriendlyExpression = GetProperty(o.Properties, "FriendlyExpression"),
                    EvaluationOrder = ParseInt(GetProperty(o.Properties, "EvaluationOrder")),
                })
                .OrderBy(c => c.EvaluationOrder ?? int.MaxValue)
                .ToList();
            conditionalSplit = new ConditionalSplitPayload
            {
                DefaultOutputName = defaultOutput?.Name,
                Cases = cases,
            };
        }

        return new PipelineComponentSpec
        {
            RefId = compEl.Attr("refId") ?? "",
            Name = compEl.Attr("name") ?? "",
            Description = compEl.Attr("description"),
            ComponentClassId = componentClassId,
            ContactInfo = compEl.Attr("contactInfo"),
            Version = compEl.Attr("version"),
            LocaleId = compEl.Attr("localeId"),
            ValidateExternalMetadata = compEl.AttrBool("validateExternalMetadata"),
            UsesDispositions = compEl.AttrBool("usesDispositions"),
            Properties = properties,
            Connections = connections,
            Inputs = inputs,
            Outputs = outputs,
            FlatFileSource = flatFileSource,
            OleDbDestination = oleDbDestination,
            OleDbSource = oleDbSource,
            Lookup = lookup,
            ConditionalSplit = conditionalSplit,
        };
    }

    /// <summary>
    /// Best-effort parse of a Lookup's <c>ReferenceMetadataXml</c> custom property -- an
    /// internal, undocumented nested-XML-in-a-string format (see <see cref="LookupPayload"/>'s
    /// doc comment). Returns an empty list on anything that doesn't parse as XML or doesn't
    /// have the expected shape, rather than throwing -- this is exactly the kind of internal
    /// format CLAUDE.md trap 12 warns isn't safe to assume stable across SSIS versions.
    /// </summary>
    private static List<LookupReferenceColumnSpec> ParseLookupReferenceColumns(string? referenceMetadataXml)
    {
        if (string.IsNullOrWhiteSpace(referenceMetadataXml)) return [];
        try
        {
            var doc = XDocument.Parse(referenceMetadataXml);
            return doc.Descendants("referenceColumn")
                .Select(c => new LookupReferenceColumnSpec
                {
                    Name = c.Attr("name") ?? "",
                    DataType = c.Attr("dataType"),
                    Length = c.AttrInt("length"),
                    Precision = c.AttrInt("precision"),
                    Scale = c.AttrInt("scale"),
                })
                .Where(c => c.Name.Length > 0)
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    private static PipelineInputSpec ReadInput(XElement inputEl) => new()
    {
        RefId = inputEl.Attr("refId") ?? "",
        Name = inputEl.Attr("name") ?? "",
        Description = inputEl.Attr("description"),
        ErrorRowDisposition = inputEl.Attr("errorRowDisposition"),
        TruncationRowDisposition = inputEl.Attr("truncationRowDisposition"),
        HasSideEffects = inputEl.AttrBool("hasSideEffects"),
        Columns = (inputEl.Element("inputColumns")?.Elements("inputColumn") ?? [])
            .Select(c => new PipelineInputColumnSpec
            {
                RefId = c.Attr("refId") ?? "",
                CachedName = c.Attr("cachedName") ?? "",
                CachedDataType = c.Attr("cachedDataType"),
                CachedLength = c.AttrInt("cachedLength"),
                CachedPrecision = c.AttrInt("cachedPrecision"),
                CachedScale = c.AttrInt("cachedScale"),
                CachedCodePage = c.AttrInt("cachedCodePage"),
                LineageId = c.Attr("lineageId") ?? "",
                ExternalMetadataColumnId = c.Attr("externalMetadataColumnId"),
                Properties = ReadProperties(c.Element("properties")),
            })
            .ToList(),
        ExternalMetadataColumns = ReadExternalMetadataColumns(inputEl.Element("externalMetadataColumns")),
        Properties = ReadProperties(inputEl.Element("properties")),
    };

    private static PipelineOutputSpec ReadOutput(XElement outputEl) => new()
    {
        RefId = outputEl.Attr("refId") ?? "",
        Name = outputEl.Attr("name") ?? "",
        Description = outputEl.Attr("description"),
        IsErrorOut = outputEl.AttrBool("isErrorOut"),
        SynchronousInputId = outputEl.Attr("synchronousInputId"),
        ExclusionGroup = outputEl.Attr("exclusionGroup"),
        DeleteOutputOnPathDetached = outputEl.AttrBool("deleteOutputOnPathDetached"),
        Columns = (outputEl.Element("outputColumns")?.Elements("outputColumn") ?? [])
            .Select(c =>
            {
                var props = ReadProperties(c.Element("properties"));
                return new PipelineOutputColumnSpec
                {
                    RefId = c.Attr("refId") ?? "",
                    Name = c.Attr("name") ?? "",
                    DataType = c.Attr("dataType"),
                    Length = c.AttrInt("length"),
                    Precision = c.AttrInt("precision"),
                    Scale = c.AttrInt("scale"),
                    CodePage = c.AttrInt("codePage"),
                    LineageId = c.Attr("lineageId") ?? "",
                    ExternalMetadataColumnId = c.Attr("externalMetadataColumnId"),
                    ErrorRowDisposition = c.Attr("errorRowDisposition"),
                    TruncationRowDisposition = c.Attr("truncationRowDisposition"),
                    ErrorOrTruncationOperation = c.Attr("errorOrTruncationOperation"),
                    SpecialFlags = c.AttrInt("specialFlags"),
                    Properties = props,
                    Expression = GetProperty(props, "Expression"),
                    FriendlyExpression = GetProperty(props, "FriendlyExpression"),
                };
            })
            .ToList(),
        ExternalMetadataColumns = ReadExternalMetadataColumns(outputEl.Element("externalMetadataColumns")),
        Properties = ReadProperties(outputEl.Element("properties")),
    };

    private static List<PipelineExternalMetadataColumnSpec> ReadExternalMetadataColumns(XElement? container) =>
        (container?.Elements("externalMetadataColumn") ?? [])
            .Select(c => new PipelineExternalMetadataColumnSpec
            {
                RefId = c.Attr("refId") ?? "",
                Name = c.Attr("name") ?? "",
                DataType = c.Attr("dataType"),
                Length = c.AttrInt("length"),
                Precision = c.AttrInt("precision"),
                Scale = c.AttrInt("scale"),
                CodePage = c.AttrInt("codePage"),
            })
            .ToList();

    private static List<PipelinePropertySpec> ReadProperties(XElement? container) =>
        (container?.Elements("property") ?? [])
            .Select(p => new PipelinePropertySpec
            {
                Name = p.Attr("name") ?? "",
                DataType = p.Attr("dataType"),
                Description = p.Attr("description"),
                Value = p.Value,
            })
            .ToList();

    private static List<PipelineColumnMappingSpec> BuildColumnMappings(
        IEnumerable<(string ColumnName, string? ExternalMetadataColumnId)> columns,
        List<PipelineExternalMetadataColumnSpec> externalColumns)
    {
        var byRefId = externalColumns.ToDictionary(e => e.RefId, e => e);
        var mappings = new List<PipelineColumnMappingSpec>();
        foreach (var (columnName, externalId) in columns)
        {
            if (externalId is null || !byRefId.TryGetValue(externalId, out var ext)) continue;
            mappings.Add(new PipelineColumnMappingSpec
            {
                ComponentColumnName = columnName,
                ExternalColumnName = ext.Name,
                ExternalDataType = ext.DataType,
            });
        }
        return mappings;
    }

    private static string? GetProperty(List<PipelinePropertySpec> properties, string name) =>
        NullIfEmpty(properties.FirstOrDefault(p => p.Name == name)?.Value);

    private static bool? ParseBool(string? raw) =>
        raw switch { "true" => true, "false" => false, _ => null };

    private static int? ParseInt(string? raw) =>
        int.TryParse(raw, out var v) ? v : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
