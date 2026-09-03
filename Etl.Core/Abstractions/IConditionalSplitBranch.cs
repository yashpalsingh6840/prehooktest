namespace Etl.Core.Abstractions;

/// <summary>
/// One branch of a Conditional Split: buffers the rows routed to it, then bulk-inserts them.
/// Deliberately NOT generic in TEntity at this level -- ConditionalSplitStep holds a list of
/// these across branches whose destination tables (and so TEntity types) can differ, and a
/// heterogeneous list needs a shared, non-generic-in-TEntity interface to live in.
/// </summary>
public interface IConditionalSplitBranch<in TRow>
{
    string Name { get; }

    /// <summary>Maps and buffers one routed row. Never writes to the database itself --
    /// see <see cref="FlushAsync"/>.</summary>
    void Add(TRow row, in RowContext ctx);

    /// <summary>Bulk-inserts every row buffered so far and returns the row count written.
    /// A branch that received zero rows writes nothing and returns 0.</summary>
    Task<long> FlushAsync(IUnitOfWork uow, CancellationToken ct);
}
