using System.Diagnostics;
using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Pipeline;

/// <summary>
/// One Data Flow Task shaped as a Multicast: one source, every row copied UNCONDITIONALLY to
/// every branch (unlike ConditionalSplitStep, there is no IRowRouter -- no routing decision at
/// all). Otherwise identical to ConditionalSplitStep, including the same buffer-then-flush
/// tradeoff (IUnitOfWork's one shared connection/transaction can't run two SqlBulkCopy operations
/// concurrently, so every branch's rows are collected before any branch's copy starts) and reuse
/// of the same IConditionalSplitBranch&lt;TRow&gt; abstraction -- a Multicast branch is exactly a
/// Conditional Split branch that never gets asked "does this row belong to you", and branches can
/// differ in destination TYPE, not just table (the real evidenced case fans to both an OLE DB
/// Destination and a Flat File Destination from the same Multicast).
/// </summary>
public sealed class MulticastStep<TRow>(
    string name,
    IRowSource<TRow> source,
    IReadOnlyList<IConditionalSplitBranch<TRow>> branches,
    ILogger<MulticastStep<TRow>> logger) : ILoadTask
{
    public string Name => name;

    public async Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        long read = 0;

        await foreach (var row in source.ReadAsync(ct))
        {
            var rowCtx = new RowContext(++read, load.StartedAtUtc, source.Name);
            foreach (var branch in branches)
                branch.Add(row, in rowCtx);
        }

        // Fails before the package's transaction commits (still inside PackageRunner's try
        // block) rather than silently succeeding with an empty load -- same rule as
        // DataFlowStep/ConditionalSplitStep.
        if (read == 0)
            throw new InvalidOperationException(
                $"{name}: source '{source.Name}' produced zero rows -- refusing to commit an empty load.");

        long written = 0;
        foreach (var branch in branches)
        {
            var branchWritten = await branch.FlushAsync(uow, ct);
            written += branchWritten;
            logger.LogInformation("{Step}/{Branch}: {Rows:N0} rows written", name, branch.Name, branchWritten);
        }

        logger.LogInformation("{Step}: {Read:N0} read, {Written:N0} written across {Branches} branch(es) in {Ms} ms",
            name, read, written, branches.Count, stopwatch.ElapsedMilliseconds);

        return new StepResult(name, read, written, stopwatch.Elapsed);
    }
}
