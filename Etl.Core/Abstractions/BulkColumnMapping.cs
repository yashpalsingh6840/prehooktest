namespace Etl.Core.Abstractions;

/// <summary>One SqlBulkCopy column mapping: pipeline buffer property name -> destination column name.</summary>
public sealed record BulkColumnMapping(string SourcePropertyName, string DestinationColumnName);
