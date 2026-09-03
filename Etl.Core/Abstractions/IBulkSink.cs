namespace Etl.Core.Abstractions;

/// <summary>An OLE DB Destination running in fast-load mode -- here, always SqlBulkCopy.</summary>
public interface IBulkSink<TEntity> where TEntity : class
{
    /// <summary>Streams <paramref name="rows"/> to the destination table and returns the row count written.</summary>
    Task<long> WriteAsync(IUnitOfWork uow, IAsyncEnumerable<TEntity> rows, CancellationToken ct);
}
