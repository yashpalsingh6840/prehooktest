using System.Data.Common;
using Etl.Core.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Data;

/// <summary>
/// An OLE DB Destination configured <c>ErrorRowDisposition=RedirectRow</c>: a row that fails to
/// insert doesn't abort the load -- it's mapped and sent to a second, caller-supplied sink instead,
/// while every other row lands normally.
/// </summary>
/// <remarks>
/// Inserts row by row rather than via <see cref="SqlBulkSink{TEntity}"/>/SqlBulkCopy, and bypasses
/// EF's change tracker (a raw parameterized <c>INSERT</c>, not <c>Add()</c>+<c>SaveChangesAsync()</c>).
/// Both are necessary, not stylistic: SqlBulkCopy has no per-row failure signal (one bad row aborts
/// the whole batch), and a destination resolved with no confident primary key is emitted
/// <c>HasNoKey()</c>, which EF's tracker can't handle at all.
///
/// Catches <see cref="DbException"/> (not just <c>SqlException</c>) so this is unit-testable without
/// a real database. On the redirected row, <c>ErrorCode</c> is filled from
/// <see cref="SqlException.Number"/> when available (SSIS's own OLE DB HRESULT has no equivalent
/// here); <c>ErrorColumn</c> is left to the caller's <c>mapToError</c> delegate. If the error sink's
/// own write throws, that propagates and aborts the run -- there's no second level of redirect.
/// </remarks>
public sealed class RedirectingSqlSink<TEntity, TErrorEntity>(
    IBulkSink<TErrorEntity> errorSink,
    Func<TEntity, DbException, TErrorEntity> mapToError,
    ILogger<RedirectingSqlSink<TEntity, TErrorEntity>> logger) : IBulkSink<TEntity>
    where TEntity : class
    where TErrorEntity : class
{
    public async Task<long> WriteAsync(IUnitOfWork uow, IAsyncEnumerable<TEntity> rows, CancellationToken ct)
    {
        var map = EntityTableMap.For(uow.Context, typeof(TEntity));
        var insertSql = BuildInsertSql(map);

        long written = 0;
        List<TErrorEntity>? errorRows = null;

        await foreach (var entity in rows.WithCancellation(ct))
        {
            var values = map.Columns.Select(c => c.GetValue(entity)).ToArray();
            try
            {
                await uow.ExecuteSqlAsync(insertSql, values, ct);
                written++;
            }
            catch (DbException ex)
            {
                logger.LogWarning(ex, "{Table}: row redirected on insert failure ({Message})", map.Table, ex.Message);
                (errorRows ??= []).Add(mapToError(entity, ex));
            }
        }

        if (errorRows is { Count: > 0 })
        {
            var redirected = await errorSink.WriteAsync(uow, ToAsyncEnumerable(errorRows), ct);
            logger.LogInformation("{Table}: {Count:N0} row(s) redirected", map.Table, redirected);
        }

        logger.LogInformation("{Table}: row-by-row insert complete, {Rows:N0} row(s), {Errors:N0} redirected",
            map.Table, written, errorRows?.Count ?? 0);
        return written;
    }

    /// <summary>Built once per call, not per row. Columns are name-based, never ordinal.</summary>
    private static string BuildInsertSql(EntityTableMap map)
    {
        var columnList = string.Join(", ", map.Columns.Select(c => $"[{c.ColumnName}]"));
        var placeholders = string.Join(", ", map.Columns.Select((_, i) => $"{{{i}}}"));
        return $"INSERT INTO {map.QuotedName} ({columnList}) VALUES ({placeholders})";
    }

    private static async IAsyncEnumerable<TErrorEntity> ToAsyncEnumerable(List<TErrorEntity> rows)
    {
        foreach (var row in rows)
            yield return row;

        await Task.CompletedTask;
    }
}

/// <summary>Extracts <see cref="SqlException.Number"/> for a generated <c>mapToError</c> delegate's
/// own <c>ErrorCode</c>, so every generated error-map uses the same tested rule.</summary>
public static class DbExceptionErrorCode
{
    public static int Resolve(DbException ex) => ex is SqlException sqlEx ? sqlEx.Number : -1;
}
