using System.Diagnostics;
using System.Runtime.CompilerServices;
using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Pipeline;

/// <summary>
/// One Data Flow Task: Source -> Transform -> Sink, streamed lazily end to end (no intermediate
/// List&lt;TEntity&gt;). Every package's data flow is an instance of this -- new packages add no new
/// plumbing, only a new TRow/TEntity/source/transform/sink combination.
/// </summary>
public sealed class DataFlowStep<TRow, TEntity>(
    string name,
    IRowSource<TRow> source,
    IRowTransform<TRow, TEntity> transform,
    IBulkSink<TEntity> sink,
    ILogger<DataFlowStep<TRow, TEntity>> logger) : ILoadTask
    where TEntity : class
{
    public string Name => name;

    public async Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        long read = 0;

        async IAsyncEnumerable<TEntity> Project([EnumeratorCancellation] CancellationToken enumCt)
        {
            await foreach (var row in source.ReadAsync(enumCt))
            {
                var rowCtx = new RowContext(++read, load.StartedAtUtc, source.Name);
                yield return transform.Map(row, in rowCtx);
            }
        }

        var written = await sink.WriteAsync(uow, Project(ct), ct);

        // Fails before the package's transaction commits (still inside the generated Program.cs's
        // own try block) rather than silently succeeding with an empty load.
        if (read == 0)
            throw new InvalidOperationException(
                $"{name}: source '{source.Name}' produced zero rows -- refusing to commit an empty load.");

        logger.LogInformation("{Step}: {Read:N0} read, {Written:N0} written in {Ms} ms",
            name, read, written, stopwatch.ElapsedMilliseconds);

        return new StepResult(name, read, written, stopwatch.Elapsed);
    }
}
