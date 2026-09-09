using System.Data.Common;
using Etl.Core.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Data;

/// <summary>
/// An OLE DB Destination whose own SSIS input is configured <c>ErrorRowDisposition=RedirectRow</c>:
/// a row that fails to insert does not abort the load -- it is mapped and sent to a second,
/// caller-supplied sink instead, while every other row still lands normally.
/// </summary>
/// <remarks>
/// <see cref="SqlBulkSink{TEntity}"/> (what every OTHER destination in a generated package uses)
/// has no way to catch a single bad row: SqlBulkCopy has no per-row failure signal at all, and a
/// failure aborts the whole in-flight write. So THIS destination inserts row by row instead --
/// a deliberate, stated trade of SqlBulkCopy's speed for real per-row redirect, scoped to exactly
/// this one destination; every other destination in the same package is unaffected.
///
/// Row-by-row insert also has to bypass EF Core's own change tracker entirely (a raw parameterized
/// <c>INSERT</c> via <see cref="IUnitOfWork.ExecuteSqlAsync(string, object?[], CancellationToken)"/>,
/// not <c>Context.Set&lt;TEntity&gt;().Add()</c> + <c>SaveChangesAsync()</c>) -- not as a stylistic
/// choice, but because a destination entity resolved with no confident primary key is emitted
/// <c>HasNoKey()</c> (see <c>DbContextEmitter</c>), and EF Core cannot track or insert a keyless
/// entity type via its change-tracking APIs at all. Bypassing EF's tracker is exactly what
/// <see cref="SqlBulkSink{TEntity}"/> already does for the same reason, via the same
/// <see cref="EntityTableMap"/> this class reuses.
///
/// Catches <see cref="DbException"/>, not <c>SqlException</c> specifically -- a real, deliberate
/// broadening (any ADO.NET provider failure redirects the row, not only a SQL Server one), and it
/// is what makes this class unit-testable with a fake, non-sealed exception type instead of a real
/// database. <c>ErrorCode</c> on the redirected row is populated from <c>SqlException.Number</c>
/// when the failure IS a real SqlException (the overwhelmingly common case) -- SSIS's own
/// <c>ErrorCode</c> is a native OLE DB HRESULT, which this rewrite (writing via
/// <c>Microsoft.Data.SqlClient</c>, a different driver stack from the OLE DB provider dtexec uses)
/// cannot reproduce bit-for-bit; the real SQL Server error number is a genuinely meaningful,
/// documented value in its place, not a guess. <c>ErrorColumn</c> is left to the caller's own
/// <c>mapToError</c> delegate -- SSIS resolves it to a column lineage id, which has no equivalent
/// here; a caller that wants a best-effort column name can parse <c>DbException.Message</c> itself
/// (SQL Server's own error text routinely names the offending column for common failures such as a
/// NULL-constraint violation or a truncation on SQL Server 2019+).
///
/// If the error sink's own write throws, that exception propagates and aborts the run -- matching
/// SSIS's own behaviour when a row cannot even be redirected successfully. There is no second level
/// of redirect.
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

    /// <summary>Built once per call, not per row -- name-based column list (never ordinal), matching
    /// <see cref="SqlBulkSink{TEntity}"/>'s own convention of never trusting property order.</summary>
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

/// <summary>Extracts <see cref="SqlException.Number"/> when <paramref name="ex"/> is a real
/// SqlException, for a generated <c>mapToError</c> delegate's own <c>ErrorCode</c> assignment --
/// kept here rather than inline so every generated error-map uses the identical, tested rule.</summary>
public static class DbExceptionErrorCode
{
    public static int Resolve(DbException ex) => ex is SqlException sqlEx ? sqlEx.Number : -1;
}
