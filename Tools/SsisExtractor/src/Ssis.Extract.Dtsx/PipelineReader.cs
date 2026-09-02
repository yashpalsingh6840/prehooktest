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

    internal static PipelineComponentSpec ReadComponent(XElement compEl, Dictionary<string, string> cmRefIdToName)
    {
        var rawComponentClassId = compEl.Attr("componentClassID") ?? "";
        var componentClassId = rawComponentClassId;
        string? normalizedFromRaw = null;
        if (LegacyComponentIds.TryNormalize(rawComponentClassId, out var canonical))
        {
            componentClassId = canonical;
            normalizedFromRaw = rawComponentClassId;
        }

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
        FlatFileDestinationPayload? flatFileDestination = null;
        OleDbDestinationPayload? oleDbDestination = null;
        OleDbSourcePayload? oleDbSource = null;
        LookupPayload? lookup = null;
        ConditionalSplitPayload? conditionalSplit = null;
        ScriptComponentPayload? scriptComponent = null;
        AdoNetDestinationPayload? adoNetDestination = null;
        AdoNetSourcePayload? adoNetSource = null;
        DataConvertPayload? dataConvert = null;
        SortPayload? sort = null;
        MergeJoinPayload? mergeJoin = null;
        ExcelSourcePayload? excelSource = null;
        OleDbCommandPayload? oleDbCommand = null;
        AggregatePayload? aggregate = null;
        RowCountPayload? rowCount = null;

        // Script Component's own componentClassID is the generic "Microsoft.ManagedComponentHost"
        // -- not a Script-Component-specific ID the way every other bespoke type here is --
        // confirmed real via the object model (ComponentPayloads.cs's own doc comment).
        // UserComponentTypeName is what actually disambiguates it.
        if (GetProperty(properties, "UserComponentTypeName") == "Microsoft.ScriptComponentHost")
        {
            var sourceCodeProp = properties.FirstOrDefault(p => p.Name == "SourceCode");
            var binaryCodeProp = properties.FirstOrDefault(p => p.Name == "BinaryCode");
            var sourceCodeItems = sourceCodeProp?.ArrayElements ?? [];
            var hasBinaryCode = binaryCodeProp?.ArrayElements.Count > 0;
            scriptComponent = new ScriptComponentPayload
            {
                Language = GetProperty(properties, "ScriptLanguage"),
                ProjectName = GetProperty(properties, "VSTAProjectName"),
                ReadOnlyVariables = SplitCommaVariableList(GetProperty(properties, "ReadOnlyVariables")),
                ReadWriteVariables = SplitCommaVariableList(GetProperty(properties, "ReadWriteVariables")),
                SourceCodeItems = sourceCodeItems,
                SourceFiles = ParseScriptComponentSourceFiles(sourceCodeItems),
                HasBinaryCode = hasBinaryCode,
                SourceStripped = sourceCodeItems.Count == 0 && hasBinaryCode,
            };
        }
        // ADO NET Destination/Source share Script Component's own discrimination shape --
        // componentClassID is the generic "Microsoft.ManagedComponentHost", UserComponentTypeName
        // is what actually says which one -- confirmed real via RBC_Demo_ETL's own
        // Package_Exports.dtsx (ADO_DST_ExportLog / ADO_SRC_Customers), not guessed from either
        // name's surface similarity to the OLE DB pair.
        else if (GetProperty(properties, "UserComponentTypeName") == "Microsoft.ADONETDestination")
        {
            var mainInput = inputs.FirstOrDefault();
            adoNetDestination = new AdoNetDestinationPayload
            {
                ConnectionName = connectionName,
                TableOrViewName = NullIfEmpty(GetProperty(properties, "TableOrViewName")),
                BatchSize = ParseInt(GetProperty(properties, "BatchSize")),
                CommandTimeout = ParseInt(GetProperty(properties, "CommandTimeout")),
                UseBulkInsertWhenPossible = ParseBool(GetProperty(properties, "UseBulkInsertWhenPossible")),
                ColumnMappings = mainInput is null ? [] : BuildColumnMappings(
                    mainInput.Columns.Select(c => (c.CachedName, c.ExternalMetadataColumnId)),
                    mainInput.ExternalMetadataColumns),
            };
        }
        else if (GetProperty(properties, "UserComponentTypeName") == "Microsoft.DataReaderSourceAdapter")
        {
            var mainOutput = outputs.FirstOrDefault(o => o.IsErrorOut != true);
            adoNetSource = new AdoNetSourcePayload
            {
                ConnectionName = connectionName,
                TableOrViewName = NullIfEmpty(GetProperty(properties, "TableOrViewName")),
                SqlCommand = NullIfEmpty(GetProperty(properties, "SqlCommand")),
                AccessMode = ParseInt(GetProperty(properties, "AccessMode")),
                CommandTimeout = ParseInt(GetProperty(properties, "CommandTimeout")),
                ColumnMappings = mainOutput is null ? [] : BuildColumnMappings(
                    mainOutput.Columns.Select(c => (c.Name, c.ExternalMetadataColumnId)),
                    mainOutput.ExternalMetadataColumns),
            };
        }
        else if (componentClassId == "Microsoft.FlatFileSource")
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
        else if (componentClassId == "Microsoft.FlatFileDestination")
        {
            var mainInput = inputs.FirstOrDefault();
            flatFileDestination = new FlatFileDestinationPayload
            {
                ConnectionName = connectionName,
                Overwrite = ParseBool(GetProperty(properties, "Overwrite")),
                Header = NullIfEmpty(GetProperty(properties, "Header")),
                EscapeQualifier = ParseBool(GetProperty(properties, "EscapeQualifier")),
                ColumnMappings = mainInput is null ? [] : BuildColumnMappings(
                    mainInput.Columns.Select(c => (c.CachedName, c.ExternalMetadataColumnId)),
                    mainInput.ExternalMetadataColumns),
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
        else if (componentClassId == "Microsoft.ExcelSource")
        {
            var mainOutput = outputs.FirstOrDefault(o => o.IsErrorOut != true);
            excelSource = new ExcelSourcePayload
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
        else if (componentClassId == "Microsoft.OLEDBCommand")
        {
            var mainInput = inputs.FirstOrDefault();
            oleDbCommand = new OleDbCommandPayload
            {
                ConnectionName = connectionName,
                SqlCommand = NullIfEmpty(GetProperty(properties, "SqlCommand")),
                ParameterMapping = NullIfEmpty(GetProperty(properties, "ParameterMapping")),
                CommandTimeout = ParseInt(GetProperty(properties, "CommandTimeout")),
                Parameters = (mainInput?.Columns ?? [])
                    .Select(c => new OleDbCommandParameterSpec { Name = c.CachedName, DataType = c.CachedDataType })
                    .ToList(),
                ColumnMappings = mainInput is null ? [] : BuildColumnMappings(
                    mainInput.Columns.Select(c => (c.CachedName, c.ExternalMetadataColumnId)),
                    mainInput.ExternalMetadataColumns),
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
                TreatDuplicateKeysAsError = ParseBool(GetProperty(properties, "TreatDuplicateKeysAsError")),
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
        else if (componentClassId == "Microsoft.DataConvert")
        {
            var mainOutput = outputs.FirstOrDefault(o => o.IsErrorOut != true);
            dataConvert = new DataConvertPayload
            {
                Columns = (mainOutput?.Columns ?? []).Select(c => new DataConversionColumnSpec
                {
                    OutputColumnName = c.Name,
                    SourceColumnLineageId = StripLineageRef(GetProperty(c.Properties, "SourceInputColumnLineageID")),
                    TargetDataType = c.DataType,
                    Length = c.Length,
                    Precision = c.Precision,
                    Scale = c.Scale,
                    CodePage = c.CodePage,
                    ErrorRowDisposition = c.ErrorRowDisposition,
                    TruncationRowDisposition = c.TruncationRowDisposition,
                    FastParse = ParseBool(GetProperty(c.Properties, "FastParse")),
                }).ToList(),
            };
        }
        else if (componentClassId == "Microsoft.Sort")
        {
            // The key designation (NewSortKeyPosition, 0 = passthrough) lives on Sort's own
            // INPUT columns, not its output columns -- confirmed real from RBC_Demo_ETL's own
            // SORT_Customers/SORT_Contacts, contrary to an initial (wrong) reading of the raw
            // XML that attributed it to the output side. Each OUTPUT column instead carries its
            // own SortColumnId, a #{...} reference to the INPUT column it mirrors (matched here
            // by comparing SortColumnId's stripped value against each input column's own
            // LineageId) -- the same "resolve by lineage, not position" rule LineageBuilder's own
            // doc comment establishes for passthrough columns generally.
            var mainInput = inputs.FirstOrDefault();
            var mainOutput = outputs.FirstOrDefault(o => o.IsErrorOut != true);
            var keyPositionByInputLineageId = (mainInput?.Columns ?? [])
                .Select(c => (c.LineageId, Position: ParseInt(GetProperty(c.Properties, "NewSortKeyPosition"))))
                .Where(x => x.Position is int p && p != 0)
                .ToDictionary(x => x.LineageId, x => x.Position!.Value);

            var keys = (mainOutput?.Columns ?? [])
                .Select(c => (c.Name, SortColumnId: StripLineageRef(GetProperty(c.Properties, "SortColumnId"))))
                .Where(x => x.SortColumnId is not null && keyPositionByInputLineageId.ContainsKey(x.SortColumnId))
                .Select(x => new SortKeySpec { ColumnName = x.Name, Position = keyPositionByInputLineageId[x.SortColumnId!] })
                .OrderBy(k => Math.Abs(k.Position))
                .ToList();
            sort = new SortPayload { Keys = keys };
        }
        else if (componentClassId == "Microsoft.Aggregate")
        {
            var mainOutput = outputs.FirstOrDefault(o => o.IsErrorOut != true);
            aggregate = new AggregatePayload
            {
                Columns = (mainOutput?.Columns ?? []).Select(c => new AggregateColumnSpec
                {
                    OutputColumnName = c.Name,
                    SourceColumnLineageId = StripLineageRef(GetProperty(c.Properties, "AggregationColumnId")),
                    AggregationTypeRaw = ParseInt(GetProperty(c.Properties, "AggregationType")),
                }).ToList(),
            };
        }
        else if (componentClassId == "Microsoft.MergeJoin")
        {
            var mainOutput = outputs.FirstOrDefault(o => o.IsErrorOut != true);
            var outputColumns = new List<MergeJoinOutputColumnSpec>();
            foreach (var col in mainOutput?.Columns ?? [])
            {
                var inputColumnRefId = StripLineageRef(GetProperty(col.Properties, "InputColumnID"));
                if (inputColumnRefId is null) continue;

                PipelineInputColumnSpec? matched = null;
                string? side = null;
                foreach (var input in inputs)
                {
                    matched = input.Columns.FirstOrDefault(ic => ic.RefId == inputColumnRefId);
                    if (matched is not null)
                    {
                        side = input.Name.Contains("Left", StringComparison.Ordinal) ? "Left" : "Right";
                        break;
                    }
                }
                if (matched is null || side is null) continue; // best-effort -- an unresolved reference simply doesn't appear here; PipelineResolver reports the missing mapping downstream

                outputColumns.Add(new MergeJoinOutputColumnSpec
                {
                    OutputColumnName = col.Name,
                    Side = side,
                    SourceColumnLineageId = matched.LineageId,
                });
            }
            mergeJoin = new MergeJoinPayload
            {
                JoinTypeRaw = ParseInt(GetProperty(properties, "JoinType")),
                NumKeyColumns = ParseInt(GetProperty(properties, "NumKeyColumns")),
                TreatNullsAsEqual = ParseBool(GetProperty(properties, "TreatNullsAsEqual")),
                OutputColumns = outputColumns,
            };
        }
        else if (componentClassId == "Microsoft.RowCount")
        {
            rowCount = new RowCountPayload
            {
                VariableName = NullIfEmpty(GetProperty(properties, "VariableName")),
            };
        }

        return new PipelineComponentSpec
        {
            RefId = compEl.Attr("refId") ?? "",
            Name = compEl.Attr("name") ?? "",
            Description = compEl.Attr("description"),
            ComponentClassId = componentClassId,
            RawComponentClassId = normalizedFromRaw,
            IsUnresolvedLegacyClsid = LegacyComponentIds.IsUnresolvedClsid(rawComponentClassId),
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
            FlatFileDestination = flatFileDestination,
            OleDbDestination = oleDbDestination,
            OleDbSource = oleDbSource,
            Lookup = lookup,
            ConditionalSplit = conditionalSplit,
            ScriptComponent = scriptComponent,
            AdoNetDestination = adoNetDestination,
            AdoNetSource = adoNetSource,
            DataConvert = dataConvert,
            Sort = sort,
            MergeJoin = mergeJoin,
            ExcelSource = excelSource,
            OleDbCommand = oleDbCommand,
            Aggregate = aggregate,
            RowCount = rowCount,
        };
    }

    /// <summary>Strips a <c>SourceInputColumnLineageID</c>-style property value (its entire content is one <c>#{lineageId}</c> reference, unlike Derived Column's <c>Expression</c> which embeds refs inside a larger expression string) down to the bare lineageId. Null/unwrapped input passes through unchanged rather than throwing -- defensive, same as this file's other best-effort parses.</summary>
    private static string? StripLineageRef(string? value) =>
        value is not null && value.StartsWith("#{", StringComparison.Ordinal) && value.EndsWith('}')
            ? value[2..^1]
            : value;

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
            .Select(c =>
            {
                // Read the property bag once and promote Expression/FriendlyExpression off it,
                // exactly as ReadOutput does for an output column. An in-place ("Replace
                // <column>") Derived Column persists its expression HERE, on the readWrite input
                // column, and declares no output column at all -- so leaving these unpromoted
                // made the whole transformation invisible to codegen, conformance, testgen, the
                // expression inventory and the non-determinism manifest at once.
                var props = ReadProperties(c.Element("properties"));
                return new PipelineInputColumnSpec
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
                    UsageType = c.Attr("usageType"),
                    Expression = GetProperty(props, "Expression"),
                    FriendlyExpression = GetProperty(props, "FriendlyExpression"),
                    OutputColumnLineageId = StripLineageRef(GetProperty(props, "OutputColumnLineageID")),
                    Properties = props,
                };
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
        Dangling = outputEl.AttrBool("dangling"),
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
            .Select(p =>
            {
                var isArray = p.Attr("isArray") == "true";
                return new PipelinePropertySpec
                {
                    Name = p.Attr("name") ?? "",
                    DataType = p.Attr("dataType"),
                    Description = p.Attr("description"),
                    Value = isArray ? null : p.Value,
                    IsArray = isArray,
                    ArrayElements = isArray
                        ? (p.Element("arrayElements")?.Elements("arrayElement") ?? []).Select(e => e.Value).ToList()
                        : [],
                };
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

    /// <summary>Comma-delimited, unlike <c>DtsxPackageReader.SplitVariableList</c>'s semicolon split for Script Task's own ReadOnlyVariables/ReadWriteVariables -- confirmed a genuinely different, real convention by reading Script Component's own property description text from the object model ("Specifies a comma-separated list of read-only variables."), not assumed to match Script Task.</summary>
    /// <summary>
    /// A Script Component's <c>SourceCode</c> array is NOT a list of opaque source blobs, despite
    /// how it reads: it is a flat sequence of repeating <b>(file name, encoding, content)</b>
    /// triples -- one per VSTA project file. Confirmed 2026-08-31 against the first real
    /// SSDT-authored Script Component available to this project (RBC_Demo_ETL's own
    /// <c>SCR_CleanseCustomerRow</c>: 30 elements = 10 files, element 0 <c>ComponentWrapper.cs</c>,
    /// element 1 <c>UTF8</c>, element 2 the actual C#), which is later than the original
    /// Script Component work -- no real one existed on this machine then, so the array was
    /// modelled as opaque and written out as <c>source-&lt;index&gt;.cs</c>, half of those files
    /// being a bare file name or the literal string "UTF8".
    ///
    /// Returns empty when the count is not a clean multiple of 3 rather than guessing an
    /// alignment -- a partial/misaligned parse would silently mislabel real source, and
    /// <see cref="ScriptComponentPayload.SourceCodeItems"/> still carries the raw array for
    /// anyone who needs it.
    /// </summary>
    private static List<ScriptComponentSourceFileSpec> ParseScriptComponentSourceFiles(List<string> sourceCodeItems)
    {
        if (sourceCodeItems.Count == 0 || sourceCodeItems.Count % 3 != 0) return [];

        var files = new List<ScriptComponentSourceFileSpec>();
        for (var i = 0; i + 2 < sourceCodeItems.Count; i += 3)
        {
            files.Add(new ScriptComponentSourceFileSpec
            {
                Name = sourceCodeItems[i],
                Encoding = sourceCodeItems[i + 1],
                Content = sourceCodeItems[i + 2],
            });
        }
        return files;
    }

    private static List<string> SplitCommaVariableList(string? raw) =>
        string.IsNullOrEmpty(raw) ? [] : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
