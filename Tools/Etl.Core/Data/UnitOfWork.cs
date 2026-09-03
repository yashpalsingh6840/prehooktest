using System.Data;
using System.Data.Common;
using Etl.Core.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Data;

/// <summary>
/// The one IUnitOfWork implementation. Pulls the live SqlConnection and SqlTransaction out of
/// EF Core's own DbContext so a SqlBulkCopy sink can enlist in the exact same transaction as the
/// TRUNCATE -- otherwise the truncate and the bulk insert land on different connections/transactions
/// and atomicity is lost. Connection/Transaction are private: IUnitOfWork itself never exposes
/// them (see that interface's remarks), so this class is the only place ADO.NET types are visible.
/// </summary>
public sealed class UnitOfWork(
    DbContext context,
    ISqlBulkCopyFactory bulkCopyFactory,
    ILogger<UnitOfWork> logger) : IUnitOfWork
{
    private IDbContextTransaction? _tx;
    private string? _bindToken;

    public DbContext Context => context;

    private SqlConnection Connection =>
        context.Database.GetDbConnection() as SqlConnection
        ?? throw new InvalidOperationException(
            "Etl.Core requires Microsoft.Data.SqlClient. The DbContext's connection is " +
            $"'{context.Database.GetDbConnection().GetType().AssemblyQualifiedName}' -- " +
            "check for a System.Data.SqlClient reference or a mismatched SqlClient version.");

    private SqlTransaction? Transaction =>
        _tx?.GetDbTransaction() as SqlTransaction;

    public async Task BeginAsync(IsolationLevel level, CancellationToken ct)
    {
        if (_tx is not null)
            throw new InvalidOperationException("A transaction is already active on this UnitOfWork.");

        // BeginTransactionAsync opens the connection and pins it open for the transaction's
        // lifetime -- every subsequent EF command AND the bulk copy run on this one connection.
        _tx = await context.Database.BeginTransactionAsync(level, ct);

        logger.LogDebug("Transaction {TransactionId} started ({IsolationLevel}) on {Server}/{Database}",
            _tx.TransactionId, level, Connection.DataSource, Connection.Database);
    }

    public Task<int> ExecuteSqlAsync(string sql, CancellationToken ct) =>
        context.Database.ExecuteSqlRawAsync(sql, ct);

    public Task<int> ExecuteSqlAsync(string sql, object?[] parameters, CancellationToken ct) =>
        context.Database.ExecuteSqlRawAsync(sql, parameters.Select(p => p ?? DBNull.Value), ct);

    public async Task<long> BulkInsertAsync(
        string destinationTable,
        DbDataReader reader,
        IReadOnlyList<BulkColumnMapping> columnMappings,
        BulkCopyOptions options,
        CancellationToken ct)
    {
        var transaction = Transaction ?? throw new InvalidOperationException(
            "BulkInsertAsync requires an active UnitOfWork transaction -- call BeginAsync before running any load step.");

        // SqlBulkCopyOptions.Default (== None) is the faithful mapping of the source .dtsx files:
        //   FastLoadKeepNulls    = false -> do not set KeepNulls
        //   FastLoadKeepIdentity = false -> do not set KeepIdentity
        //   FastLoadOptions      = ""    -> do not set CheckConstraints / FireTriggers
        // UseInternalTransaction must NEVER be set -- SqlBulkCopy throws when combined with
        // an external transaction, which is exactly what we're passing.
        var bulkOptions = SqlBulkCopyOptions.Default;
        if (options.UseTableLock) bulkOptions |= SqlBulkCopyOptions.TableLock;
        if (options.CheckConstraints) bulkOptions |= SqlBulkCopyOptions.CheckConstraints;

        await using var bulk = bulkCopyFactory.Create(Connection, bulkOptions, transaction);
        bulk.DestinationTableName = destinationTable;
        bulk.BatchSize = options.BatchSize;
        bulk.BulkCopyTimeout = options.TimeoutSeconds;
        bulk.EnableStreaming = true;
        bulk.NotifyAfter = options.NotifyAfter;

        foreach (var mapping in columnMappings)
            bulk.AddColumnMapping(mapping.SourcePropertyName, mapping.DestinationColumnName);

        bulk.SqlRowsCopied += (_, e) =>
            logger.LogInformation("{Table}: {Rows:N0} rows copied", destinationTable, e.RowsCopied);

        await bulk.WriteToServerAsync(reader, ct);
        return bulk.RowsCopied;
    }

    public async Task<string> GetBindTokenAsync(CancellationToken ct)
    {
        if (_bindToken is not null) return _bindToken;

        var transaction = Transaction ?? throw new InvalidOperationException(
            "GetBindTokenAsync requires an active UnitOfWork transaction -- call BeginAsync first.");

        await using var command = Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DECLARE @token varchar(255); EXEC sp_getbindtoken @token OUTPUT; SELECT @token;";
        _bindToken = (string)(await command.ExecuteScalarAsync(ct))!;

        logger.LogDebug("Transaction {TransactionId}: bind token issued for cross-connection reads", _tx!.TransactionId);
        return _bindToken;
    }

    public Task<int> ExecuteSqlWithoutTransactionAsync(string sql, CancellationToken ct)
    {
        // Deliberately strict rather than charitable: a failure handler that quietly joined the
        // package transaction would be rolled back with everything else and write nothing, which
        // is the one outcome this position exists to prevent.
        if (context.Database.CurrentTransaction is not null)
            throw new InvalidOperationException(
                "ExecuteSqlWithoutTransactionAsync was called while a transaction is still active. " +
                "It is only valid after RollbackAsync -- a failure handler must not enlist in the " +
                "transaction being discarded.");

        return context.Database.ExecuteSqlRawAsync(sql, ct);
    }

    public Task CommitAsync(CancellationToken ct) =>
        _tx?.CommitAsync(ct) ?? throw NoTransaction();

    public Task RollbackAsync(CancellationToken ct) =>
        _tx?.RollbackAsync(ct) ?? throw NoTransaction();

    public async ValueTask DisposeAsync()
    {
        if (_tx is not null) await _tx.DisposeAsync();
        await context.DisposeAsync();
    }

    private static InvalidOperationException NoTransaction() =>
        new("No transaction is active on this UnitOfWork -- call BeginAsync first.");
}
