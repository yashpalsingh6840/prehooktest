using Etl.Core.Abstractions;

namespace Etl.Core.Pipeline;

/// <summary>
/// One <c>Microsoft.SCD</c> output's downstream chain, covering all three shapes the real evidenced
/// package uses, with no duplicated buffering logic:
/// <list type="bullet">
/// <item><b>insert only</b> -- <paramref name="sink"/> set, no command. The <c>New Output</c> shape.</item>
/// <item><b>command only</b> -- a command set, no sink. The <c>Changing Attribute Updates Output</c>
/// shape (a terminal per-row <c>UPDATE</c>, no destination at all).</item>
/// <item><b>command then insert</b> -- both. The <c>Historical Attribute Inserts Output</c> shape
/// (close the old dimension row, then insert the new one through the shared destination).</item>
/// </list>
///
/// <para>The insert half is delegated wholesale to an <see cref="IConditionalSplitBranch{TRow}"/> --
/// i.e. to the very same <see cref="ConditionalSplitBranch{TRow,TEntity}"/> Conditional Split,
/// Multicast and Percentage Sampling already use -- so the map/buffer/bulk-copy path exists exactly
/// once in this library. This type adds only the per-row command half and the two-phase ordering
/// <see cref="IScdBranch{TRow}"/> exists for.</para>
///
/// <para>Command parameters are computed at <see cref="Add"/> time, not retained as rows: a branch
/// that only runs a command therefore holds one small <c>object?[]</c> per row rather than the whole
/// row, and a branch that does both never buffers the row twice.</para>
/// </summary>
public sealed class ScdBranch<TRow>(
    string name,
    IConditionalSplitBranch<TRow>? sink,
    string? commandSql,
    Func<TRow, object?[]>? commandParameters) : IScdBranch<TRow>
{
    private readonly List<object?[]> _commandBuffer = [];

    public string Name => name;

    public void Add(TRow row, in RowContext ctx)
    {
        if (commandSql is not null && commandParameters is not null)
            _commandBuffer.Add(commandParameters(row));

        sink?.Add(row, in ctx);
    }

    public async Task<long> ExecuteCommandsAsync(IUnitOfWork uow, CancellationToken ct)
    {
        if (commandSql is null) return 0;

        long affected = 0;
        foreach (var parameters in _commandBuffer)
            affected += await uow.ExecuteSqlAsync(commandSql, parameters, ct);

        return affected;
    }

    public Task<long> FlushAsync(IUnitOfWork uow, CancellationToken ct) =>
        sink is null ? Task.FromResult(0L) : sink.FlushAsync(uow, ct);
}
