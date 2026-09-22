using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Etl.Core.Data;

/// <summary>An OLE DB Destination in fast-load mode (SqlBulkCopy). Delegates the actual copy to
/// <see cref="IUnitOfWork.BulkInsertAsync"/> instead of touching a connection/transaction directly,
/// so this is unit-testable against a fake <see cref="IUnitOfWork"/> -- no SQL Server needed.</summary>
public sealed class SqlBulkSink<TEntity>(
    IOptions<BulkCopyOptions> options,
    ILogger<SqlBulkSink<TEntity>> logger) : IBulkSink<TEntity>
    where TEntity : class
{
    public async Task<long> WriteAsync(IUnitOfWork uow, IAsyncEnumerable<TEntity> rows, CancellationToken ct)
    {
        var map = EntityTableMap.For(uow.Context, typeof(TEntity));

        // Name-based mapping, never ordinal -- ordinal mapping silently writes the wrong
        // column the day a property is reordered or one is added.
        var mappings = map.Columns
            .Select(c => new BulkColumnMapping(c.PropertyName, c.ColumnName))
            .ToArray();

        await using var reader = new ObjectDataReader<TEntity>(rows, map.Columns, ct);
        var written = await uow.BulkInsertAsync(map.QuotedName, reader, mappings, options.Value, ct);

        logger.LogInformation("{Table}: bulk copy complete, {Rows:N0} rows", map.Table, written);
        return written;
    }
}
