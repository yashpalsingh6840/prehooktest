namespace Etl.Core.Data;

/// <summary>An OLE DB Source's connection + query, resolved at DI registration time -- see
/// <see cref="SqlRowSource{TRow}"/>.</summary>
public sealed class SqlSourceOptions
{
    public required string ConnectionString { get; init; }
    public required string CommandText { get; init; }
    public int CommandTimeoutSeconds { get; init; } = 30;
}
