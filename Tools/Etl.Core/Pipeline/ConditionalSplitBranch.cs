using Etl.Core.Abstractions;

namespace Etl.Core.Pipeline;

/// <summary>
/// One Conditional Split branch: TRow -> TEntity via its own <see cref="IRowTransform{TRow,TEntity}"/>,
/// written via its own <see cref="IBulkSink{TEntity}"/>. Buffers every routed row in memory and
/// flushes them in one bulk copy -- see ConditionalSplitStep's own doc comment for why this
/// isn't streamed the way DataFlowStep is: IUnitOfWork's single shared connection/transaction
/// can't safely run more than one SqlBulkCopy at a time, so every branch's rows must be known
/// before any branch's copy starts.
/// </summary>
public sealed class ConditionalSplitBranch<TRow, TEntity>(
    string name,
    IRowTransform<TRow, TEntity> transform,
    IBulkSink<TEntity> sink) : IConditionalSplitBranch<TRow>
    where TEntity : class
{
    private readonly List<TEntity> _buffer = [];

    public string Name => name;

    public void Add(TRow row, in RowContext ctx) => _buffer.Add(transform.Map(row, in ctx));

    public async Task<long> FlushAsync(IUnitOfWork uow, CancellationToken ct) =>
        _buffer.Count == 0 ? 0 : await sink.WriteAsync(uow, ToAsyncEnumerable(), ct);

    private async IAsyncEnumerable<TEntity> ToAsyncEnumerable()
    {
        foreach (var item in _buffer)
            yield return item;

        await Task.CompletedTask;
    }
}
