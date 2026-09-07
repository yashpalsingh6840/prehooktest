using System.Diagnostics;
using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Pipeline;

/// <summary>
/// One Data Flow Task shaped as an OLE DB Command: one source, one parameterized SQL statement
/// executed ONCE PER ROW on the same shared connection/transaction as everything else in the
/// package. Unlike every other flow shape, there is no destination table, no EF entity, and no
/// IBulkSink -- the command itself is the flow's sink. Each row's command runs immediately and
/// independently (no buffering needed, unlike ConditionalSplitStep/MulticastStep), so this
/// streams genuinely end to end.
/// </summary>
public sealed class OleDbCommandStep<TRow>(
    string name,
    IRowSource<TRow> source,
    string sqlTemplate,
    Func<TRow, object?[]> parameterValues,
    ILogger<OleDbCommandStep<TRow>> logger) : ILoadTask
{
    public string Name => name;

    public async Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        long read = 0;
        long written = 0;

        await foreach (var row in source.ReadAsync(ct))
        {
            read++;
            written += await uow.ExecuteSqlAsync(sqlTemplate, parameterValues(row), ct);
        }

        // Fails before the package's transaction commits (still inside the generated Program.cs's
        // own try block) rather than silently succeeding with an empty load -- same rule as
        // DataFlowStep/ConditionalSplitStep/MulticastStep.
        if (read == 0)
            throw new InvalidOperationException(
                $"{name}: source '{source.Name}' produced zero rows -- refusing to commit an empty load.");

        logger.LogInformation("{Step}: {Read:N0} read, {Written:N0} rows affected in {Ms} ms",
            name, read, written, stopwatch.ElapsedMilliseconds);

        return new StepResult(name, read, written, stopwatch.Elapsed);
    }
}
