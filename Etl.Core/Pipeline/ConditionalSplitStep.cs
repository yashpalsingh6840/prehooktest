using System.Diagnostics;
using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Pipeline;

/// <summary>
/// One Data Flow Task shaped as a Conditional Split: one source, routed row-by-row to exactly
/// one of N branches (see IRowRouter&lt;TRow&gt;), each branch its own transform + destination.
///
/// Unlike DataFlowStep&lt;TRow,TEntity&gt;, this does NOT stream straight through to the sink --
/// IUnitOfWork exposes one shared SqlConnection/SqlTransaction for the whole package run, and
/// two SqlBulkCopy operations can't safely run concurrently against it. So every source row is
/// read and routed into its branch's in-memory buffer first (ConditionalSplitBranch.Add), and
/// only once the source is exhausted does each branch bulk-insert its own buffer, one at a time.
/// This trades true end-to-end streaming for correctness under the existing single-connection
/// design -- an explicit, accepted limitation, not an oversight.
/// </summary>
public sealed class ConditionalSplitStep<TRow>(
    string name,
    IRowSource<TRow> source,
    IRowRouter<TRow> router,
    IReadOnlyList<IConditionalSplitBranch<TRow>> branches,
    ILogger<ConditionalSplitStep<TRow>> logger) : ILoadTask
{
    public string Name => name;

    public async Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        long read = 0;

        await foreach (var row in source.ReadAsync(ct))
        {
            var rowCtx = new RowContext(++read, load.StartedAtUtc, source.Name);
            branches[router.SelectBranch(row, in rowCtx)].Add(row, in rowCtx);
        }

        // Fails before the package's transaction commits (still inside the generated Program.cs's
        // own try block) rather than silently succeeding with an empty load -- same rule as DataFlowStep.
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
