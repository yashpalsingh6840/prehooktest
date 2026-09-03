using System.Data;
using System.Diagnostics;
using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Pipeline;

/// <summary>
/// The Control Flow engine: begin transaction, run pre-load SQL, run steps strictly in order,
/// commit -- or roll back everything on any failure. This is the deliberate improvement over
/// SSIS, which auto-committed its TRUNCATE separately from the data flow: here a failure partway
/// through restores the table(s) to their prior contents instead of leaving them empty.
/// </summary>
public sealed class PackageRunner(IUnitOfWork uow, TimeProvider timeProvider, ILogger<PackageRunner> logger) : IPackageRunner
{
    public async Task<PackageResult> RunAsync(IEtlPackage package, CancellationToken ct)
    {
        var load = new LoadContext(Guid.NewGuid(), TruncateToMilliseconds(timeProvider.GetUtcNow()), package.Name);
        var stopwatch = Stopwatch.StartNew();
        var results = new List<StepResult>();

        await uow.BeginAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // Fetched here, once, while this transaction's own connection is guaranteed idle --
            // GetBindTokenAsync caches its result, so every later SqlRowSource's own (lazy) call
            // to it is a cache hit that never touches the connection again. That caching is not
            // an optimisation here, it is what makes this safe at all: a SqlRowSource feeding a
            // SqlBulkSink destination is read from WHILE that destination's own SqlBulkCopy is
            // actively streaming on this SAME connection (pulling rows as it writes) -- fetching
            // the token lazily, from inside that nested read, would issue a second command on a
            // connection SqlBulkCopy is already mid-operation on, which can never complete: a
            // real, reproduced deadlock, not a hypothetical one.
            await uow.GetBindTokenAsync(ct);

            foreach (var sql in package.PreLoadStatements)
            {
                logger.LogInformation("{Package}: executing pre-load statement: {Sql}", package.Name, sql);
                await uow.ExecuteSqlAsync(sql, ct);
            }

            foreach (var action in package.PreLoadFileActions)
            {
                logger.LogInformation("{Package}: executing pre-load file action: {Operation} {Source}{Destination}",
                    package.Name, action.Operation, action.SourcePath,
                    action.DestinationPath is null ? "" : $" -> {action.DestinationPath}");
                await FileSystemActionRunner.RunAsync(action, ct);
            }

            foreach (var step in package.Steps)
                results.Add(await step.RunAsync(uow, load, ct));

            await uow.CommitAsync(ct);
            logger.LogInformation("{Package}: succeeded in {Ms} ms", package.Name, stopwatch.ElapsedMilliseconds);
            return new PackageResult(package.Name, true, results, stopwatch.Elapsed, null);
        }
        catch (Exception ex)
        {
            await uow.RollbackAsync(CancellationToken.None);
            logger.LogError(ex, "{Package} failed after {Steps} step(s); transaction rolled back",
                package.Name, results.Count);

            var handlersRun = await RunFailureHandlersAsync(package);
            return new PackageResult(package.Name, false, results, stopwatch.Elapsed, ex)
            {
                FailureHandlersRun = handlersRun,
            };
        }
    }

    /// <summary>
    /// Runs every <see cref="FailureHandlerAction"/> after the rollback, each committing on its
    /// own. This reproduces what SSIS was measured to do for a Failure precedence constraint
    /// (dtexec, SyntheticCondConstraint.dtsx): the successor RUNS, its work is COMMITTED, and the
    /// package still fails.
    ///
    /// <para>Three deliberate choices. Handlers run AFTER the rollback, never before, so the
    /// handler's own row survives while the failed load does not. Each is isolated in its own
    /// try/catch: a handler that itself throws must not replace the original exception, which is
    /// the thing anyone diagnosing the run actually needs -- and SSIS likewise would not have
    /// erased the first failure. And <see cref="CancellationToken.None"/> is used throughout, the
    /// same choice the rollback above makes: a cancelled run still needs its failure recorded.</para>
    ///
    /// <para>One divergence from SSIS, inherited from this rewrite's deliberate design and worth
    /// stating rather than hiding: SSIS auto-committed each task separately, so a failing task's
    /// own partial work persisted alongside the handler's row. Here the whole load is rolled back
    /// and only the handler's row remains -- the same trade <see cref="PackageRunner"/> already
    /// makes for a plain failure.</para>
    /// </summary>
    private async Task<List<string>> RunFailureHandlersAsync(IEtlPackage package)
    {
        var handlersRun = new List<string>();
        foreach (var handler in package.FailureHandlers)
        {
            try
            {
                logger.LogInformation(
                    "{Package}: running failure handler {Handler} (outside the rolled-back transaction)",
                    package.Name, handler.TaskName);
                await uow.ExecuteSqlWithoutTransactionAsync(handler.Sql, CancellationToken.None);
                handlersRun.Add(handler.TaskName);
            }
            catch (Exception handlerEx)
            {
                logger.LogError(handlerEx,
                    "{Package}: failure handler {Handler} itself failed; the original failure stands",
                    package.Name, handler.TaskName);
            }
        }
        return handlersRun;
    }

    /// <summary>
    /// DATETIME2(3) rounds on insert; truncating here first means the stored value is exactly
    /// what the application computed, with no rounding surprises at the millisecond boundary.
    /// </summary>
    private static DateTime TruncateToMilliseconds(DateTimeOffset instant)
    {
        var ticks = instant.UtcDateTime.Ticks;
        return new DateTime(ticks - (ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }
}
