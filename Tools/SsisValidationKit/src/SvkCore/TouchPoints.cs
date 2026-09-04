using Ssis.Extract.Model.Analysis;

namespace Svk.Core;

public enum TouchPointKind { FlatFileSource, ExcelSource, SqlSource, SqlDestination, FlatFileDestination }

public sealed record ColumnSchema(string Name, string? PipelineDataType, int? Length, int? Precision, int? Scale);

public sealed record SourceTouchPoint(
    string ComponentName,
    string ComponentRefId,
    TouchPointKind Kind,
    List<ColumnSchema> Columns,
    string? TargetTableOrFile);

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
