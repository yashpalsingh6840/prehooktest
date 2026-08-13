namespace Ssis.Extract.Model.Pipeline;

/// <summary>One design-time column ↔ external (file/table) column mapping, derived from an input/output column's <c>externalMetadataColumnId</c> resolved against the same input/output's own <c>externalMetadataColumns</c> list -- not a separate mapping section in the source XML.</summary>
public sealed class PipelineColumnMappingSpec
{
    public required string ComponentColumnName { get; init; }
    public required string ExternalColumnName { get; init; }
    public string? ExternalDataType { get; init; }
}

/// <summary>
/// <c>Microsoft.FlatFileSource</c> (plan §4.7 table: "connection + column mapping,
/// Overwrite, header"). <see cref="ColumnMappings"/> is built from the main (non-error)
/// output's columns against their <c>externalMetadataColumns</c> -- the design-time
/// contract with the flat file's own schema.
/// </summary>
public sealed class FlatFileSourcePayload
{
    public string? ConnectionName { get; init; }

    /// <summary>Whether zero-length columns are treated as null.</summary>
    public bool? RetainNulls { get; init; }

    /// <summary>Name of an output column carrying the source file name, if configured. Empty/null means no such column is generated.</summary>
    public string? FileNameColumnName { get; init; }

    public List<PipelineColumnMappingSpec> ColumnMappings { get; init; } = [];
}

/// <summary>
/// <c>Microsoft.OLEDBDestination</c> (plan §4.7 table). <see cref="AccessMode"/> is kept as
/// the raw integer only -- the XML tags it <c>typeConverter="AccessMode"</c> but doesn't
/// spell out the enum's text values in this file, and CLAUDE.md trap 12's lesson is to not
/// hand-guess an enum without runtime/documentation evidence; a future slice can resolve it
/// via the object-model oracle (plan §6, slice 6) if needed.
/// </summary>
public sealed class OleDbDestinationPayload
{
    public string? ConnectionName { get; init; }

    /// <summary>The target table/view, e.g. "[dbo].[Employee]". Null/empty when <see cref="SqlCommand"/> is used instead (AccessMode = SQL command).</summary>
    public string? OpenRowset { get; init; }

    public string? SqlCommand { get; init; }
    public int? AccessMode { get; init; }
    public string? FastLoadOptions { get; init; }
    public bool? FastLoadKeepIdentity { get; init; }
    public bool? FastLoadKeepNulls { get; init; }
    public int? FastLoadMaxInsertCommitSize { get; init; }
    public int? CommandTimeout { get; init; }

    /// <summary>Input columns ↔ external (target table) columns, built from the main input's columns against their <c>externalMetadataColumns</c>.</summary>
    public List<PipelineColumnMappingSpec> ColumnMappings { get; init; } = [];
}
