using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Etl.Core.Data;

/// <summary>
/// An OLE DB Destination in fast-load mode. Delegates the actual SqlBulkCopy work to
/// <see cref="IUnitOfWork.BulkInsertAsync"/> rather than reaching into a connection/transaction
/// itself -- that indirection is what makes this class unit-testable against a fake IUnitOfWork,
/// no SQL Server required (see the remarks on <see cref="IUnitOfWork"/>).
/// </summary>
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
