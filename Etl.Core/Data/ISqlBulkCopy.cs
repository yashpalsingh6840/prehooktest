using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Etl.Core.Data;

/// <summary>
/// The subset of SqlBulkCopy that <see cref="Etl.Core.Hosting.EtlHost"/>'s real UnitOfWork
/// implementation needs. SqlBulkCopy is sealed, so this exists purely as a fake-able seam --
/// without it, "SqlBulkCopy is `new`'d inline" (as an earlier version did directly inside the
/// sink) is the kind of thing that can only be exercised against a live SQL Server.
/// </summary>
public interface ISqlBulkCopy : IAsyncDisposable
{
    string DestinationTableName { set; }
    int BatchSize { set; }
    int BulkCopyTimeout { set; }
    bool EnableStreaming { set; }
    int NotifyAfter { set; }
    long RowsCopied { get; }

    event SqlRowsCopiedEventHandler SqlRowsCopied;

    void AddColumnMapping(string sourceColumn, string destinationColumn);

    Task WriteToServerAsync(DbDataReader reader, CancellationToken ct);
}

/// <summary>Creates the real ISqlBulkCopy for a given connection/transaction -- the factory seam
/// itself, injected into UnitOfWork rather than having it `new SqlBulkCopy(...)` directly.</summary>
public interface ISqlBulkCopyFactory
{
    ISqlBulkCopy Create(SqlConnection connection, SqlBulkCopyOptions options, SqlTransaction transaction);
}

public sealed class SqlBulkCopyFactory : ISqlBulkCopyFactory
{
    public ISqlBulkCopy Create(SqlConnection connection, SqlBulkCopyOptions options, SqlTransaction transaction) =>
        new SqlBulkCopyAdapter(new SqlBulkCopy(connection, options, transaction));
}

internal sealed class SqlBulkCopyAdapter(SqlBulkCopy inner) : ISqlBulkCopy
{
    public string DestinationTableName { set => inner.DestinationTableName = value; }
    public int BatchSize { set => inner.BatchSize = value; }
    public int BulkCopyTimeout { set => inner.BulkCopyTimeout = value; }
    public bool EnableStreaming { set => inner.EnableStreaming = value; }
    public int NotifyAfter { set => inner.NotifyAfter = value; }
    public long RowsCopied => inner.RowsCopied;

    public event SqlRowsCopiedEventHandler SqlRowsCopied
    {
        add => inner.SqlRowsCopied += value;
        remove => inner.SqlRowsCopied -= value;
    }

    public void AddColumnMapping(string sourceColumn, string destinationColumn) =>
        inner.ColumnMappings.Add(sourceColumn, destinationColumn);

    public Task WriteToServerAsync(DbDataReader reader, CancellationToken ct) =>
        inner.WriteToServerAsync(reader, ct);

    public ValueTask DisposeAsync()
    {
        inner.Close();
        return ValueTask.CompletedTask;
    }
}
