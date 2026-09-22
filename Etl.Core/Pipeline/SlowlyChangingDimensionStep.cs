using System.Diagnostics;
using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Pipeline;

/// <summary>The six <c>Microsoft.SCD</c> outputs' downstream chains. A null entry is an output the
/// package leaves unwired -- a real, common case (the one real evidenced package wires four of six),
/// and not an error: rows routed there simply go nowhere, exactly as in SSIS.</summary>
public sealed class ScdBranches<TRow>
{
    public IScdBranch<TRow>? Unchanged { get; init; }
    public IScdBranch<TRow>? New { get; init; }
    public IScdBranch<TRow>? FixedAttribute { get; init; }
    public IScdBranch<TRow>? ChangingAttributeUpdates { get; init; }
    public IScdBranch<TRow>? HistoricalAttributeInserts { get; init; }

    internal IEnumerable<IScdBranch<TRow>> All()
    {
        if (Unchanged is not null) yield return Unchanged;
        if (New is not null) yield return New;
        if (FixedAttribute is not null) yield return FixedAttribute;
        if (ChangingAttributeUpdates is not null) yield return ChangingAttributeUpdates;
        if (HistoricalAttributeInserts is not null) yield return HistoricalAttributeInserts;
    }
}

/// <summary>
/// One Data Flow Task shaped as a <c>Microsoft.SCD</c> ("Slowly Changing Dimension"): one source, a
/// full-cache lookup of the dimension's CURRENT rows by business key, and up to five routed branches
/// -- every routing rule measured against real SSIS, see <see cref="ScdClassifier"/> for the rule set
/// and its per-rule dtexec evidence.
///
/// <para><b>The reference read reuses the existing Lookup machinery rather than inventing a second
/// one.</b> <paramref name="referenceLoader"/> is satisfied by a generated cache class emitted the
/// same way, and by the same emitter shape, as a Lookup's own full-cache preload
/// (<c>LookupCacheEmitter</c>): one query, run once, up front, into a dictionary. The SCD's own
/// <c>CurrentRowWhere</c> is composed into that query rather than applied here, so "current" means
/// exactly what the package says it means.</para>
///
/// <para><b>Duplicate business keys resolve to the FIRST row</b>, matching the measured behaviour of
/// a full-cache Lookup on this same SQL Server (<c>result.TryAdd(...)</c>, see
/// <c>LookupCacheEmitter</c>'s own note). A dimension with more than one row satisfying
/// <c>CurrentRowWhere</c> for one business key is a malformed dimension in the first place.</para>
///
/// <para><b>Branches run in TWO phases, and that is a correctness requirement, not tidiness.</b>
/// Every branch's per-row SQL commands run first (phase 1), then every branch's bulk insert (phase
/// 2). The real evidenced package's own historical branch runs
/// <c>UPDATE ... SET [EndDate] = ? WHERE [EmpId] = ? AND [EndDate] IS NULL</c> to close the outgoing
/// dimension row, and then inserts the replacement through the very same destination the
/// <c>New Output</c> branch feeds. Were an insert allowed to run first, that <c>WHERE</c> would also
/// match the row this load had just inserted, and the load would close out its own new rows. Doing
/// every close before any insert makes the outcome independent of branch ordering.</para>
///
/// <para>Buffer-then-flush, like <see cref="ConditionalSplitStep{TRow}"/> and
/// <see cref="MulticastStep{TRow}"/>, and for the same reason documented there: <see cref="IUnitOfWork"/>
/// has one shared connection/transaction, and two <c>SqlBulkCopy</c> operations cannot safely run
/// against it concurrently.</para>
/// </summary>
public sealed class SlowlyChangingDimensionStep<TRow, TKey>(
    string name,
    IRowSource<TRow> source,
    Func<CancellationToken, Task<Dictionary<TKey, object?[]>>> referenceLoader,
    Func<TRow, TKey> keySelector,
    Func<TRow, object?[]> attributeValues,
    ScdClassifier classifier,
    ScdBranches<TRow> branches,
    ILogger<SlowlyChangingDimensionStep<TRow, TKey>> logger) : ILoadTask
    where TKey : notnull
{
    public string Name => name;

    public async Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        var reference = await referenceLoader(ct);
        logger.LogInformation("{Step}: {Rows:N0} current dimension row(s) cached", name, reference.Count);

        long read = 0;
        var routed = new Dictionary<ScdRouting, long>();

        await foreach (var row in source.ReadAsync(ct))
        {
            var rowCtx = new RowContext(++read, load.StartedAtUtc, source.Name);
            var key = keySelector(row);
            var incoming = attributeValues(row);
            var match = reference.TryGetValue(key, out var referenceValues) ? referenceValues : null;

            // A ScdFixedAttributeChangeException here propagates straight out of RunAsync -- which
            // is what real SSIS does too (it fails the component, and nothing lands anywhere).
            var routing = classifier.Classify(incoming, match, key.ToString() ?? "(null)");

            Route(routing, ScdRouting.Unchanged, branches.Unchanged, row, in rowCtx, routed);
            Route(routing, ScdRouting.New, branches.New, row, in rowCtx, routed);
            Route(routing, ScdRouting.FixedAttribute, branches.FixedAttribute, row, in rowCtx, routed);
            Route(routing, ScdRouting.ChangingAttributeUpdates, branches.ChangingAttributeUpdates, row, in rowCtx, routed);
            Route(routing, ScdRouting.HistoricalAttributeInserts, branches.HistoricalAttributeInserts, row, in rowCtx, routed);
        }

        // Same rule as DataFlowStep/ConditionalSplitStep/MulticastStep -- fail before the package's
        // transaction commits rather than silently succeeding with an empty load.
        if (read == 0)
        {
            throw new InvalidOperationException(
                $"{name}: source '{source.Name}' produced zero rows -- refusing to commit an empty load.");
        }

        // Phase 1 -- every branch's per-row commands, before any insert. See this type's own doc
        // comment for why this ordering is load-bearing.
        long affected = 0;
        foreach (var branch in branches.All())
        {
            var branchAffected = await branch.ExecuteCommandsAsync(uow, ct);
            if (branchAffected > 0)
                logger.LogInformation("{Step}/{Branch}: {Rows:N0} row(s) affected by per-row command", name, branch.Name, branchAffected);
            affected += branchAffected;
        }

        // Phase 2 -- every branch's bulk insert.
        long written = 0;
        foreach (var branch in branches.All())
        {
            var branchWritten = await branch.FlushAsync(uow, ct);
            if (branchWritten > 0)
                logger.LogInformation("{Step}/{Branch}: {Rows:N0} rows written", name, branch.Name, branchWritten);
            written += branchWritten;
        }

        foreach (var (routing, count) in routed.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
            logger.LogInformation("{Step}: {Count:N0} row(s) routed to {Routing}", name, count, routing);

        logger.LogInformation("{Step}: {Read:N0} read, {Affected:N0} updated, {Written:N0} inserted in {Ms} ms",
            name, read, affected, written, stopwatch.ElapsedMilliseconds);

        return new StepResult(name, read, affected + written, stopwatch.Elapsed);
    }

    private static void Route(
        ScdRouting routing, ScdRouting flag, IScdBranch<TRow>? branch, TRow row, in RowContext ctx,
        Dictionary<ScdRouting, long> routed)
    {
        if ((routing & flag) == 0) return;

        routed[flag] = routed.TryGetValue(flag, out var n) ? n + 1 : 1;

        // A null branch is an output the package leaves unwired -- the row goes nowhere, exactly as
        // in SSIS. Counted above regardless, so the log still reports what the classifier decided.
        branch?.Add(row, in ctx);
    }
}
