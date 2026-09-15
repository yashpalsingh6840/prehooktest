namespace Etl.Core.Abstractions;

/// <summary>A destination that writes a stream of rows and reports how many landed. Implementations:
/// <see cref="Etl.Core.Data.SqlBulkSink{TEntity}"/> (SqlBulkCopy), <see cref="Etl.Core.Data.FlatFileBulkSink{TEntity}"/>
/// (a file), <see cref="Etl.Core.Data.RedirectingSqlSink{TEntity,TErrorEntity}"/> (row-by-row, with error redirect).</summary>
public interface IBulkSink<TEntity> where TEntity : class
{
    /// <summary>Streams <paramref name="rows"/> to the destination table and returns the row count written.</summary>
    Task<long> WriteAsync(IUnitOfWork uow, IAsyncEnumerable<TEntity> rows, CancellationToken ct);
}
