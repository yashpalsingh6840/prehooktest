using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Shared;

namespace Svk.Core;

public enum TouchPointKind { FlatFileSource, ExcelSource, SqlSource, SqlDestination, FlatFileDestination }

public sealed record ColumnSchema(string Name, string? PipelineDataType, int? Length, int? Precision, int? Scale);

public sealed record SourceTouchPoint(
    string ComponentName,
    string ComponentRefId,
    TouchPointKind Kind,
    List<ColumnSchema> Columns,
    string? TargetTableOrFile,
    /// <summary>Only ever populated for a FlatFileSource, and only when its own connection
    /// manager resolves -- the connection manager, not the pipeline's external-metadata columns,
    /// is the sole owner of physical layout (Delimited vs RaggedRight/FixedWidth, per-column
    /// widths). Null means "render as an ordinary delimited CSV", the pre-existing behavior.</summary>
    FlatFileFormatSpec? Format = null,
    /// <summary>Only ever populated for an ExcelSource. The EXCEL connection manager's own
    /// "Extended Properties=...HDR=YES/NO" -- resolved once here so SampleDataWriter never needs
    /// connection-manager context of its own. Null (unresolvable connection manager) is treated
    /// the same as "true" downstream -- HDR=YES is the one real evidenced default.</summary>
    bool? ExcelHasHeaderRow = null);

public sealed record DestinationTouchPoint(
    string ComponentName,
    string ComponentRefId,
    TouchPointKind Kind,
    List<ColumnSchema> Columns,
    string? TableName,
    PrimaryKeyCandidateSpec? PrimaryKey);

public sealed record LookupTouchPoint(
    string ComponentName,
    string ComponentRefId,
    string? SqlCommand,
    List<ColumnSchema> ReferenceColumns,
    (string InputColumn, string ReferenceColumn)? JoinKey);

public sealed class PackagePlan
{
    public required string PackageName { get; init; }
    public required List<SourceTouchPoint> Sources { get; init; }
    public required List<DestinationTouchPoint> Destinations { get; init; }
    public required List<LookupTouchPoint> Lookups { get; init; }
}
