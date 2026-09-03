namespace Etl.Core.Abstractions;

/// <summary>
/// A Conditional Split's routing decision. Pure and row-at-a-time, evaluated BEFORE any
/// per-branch transform -- <typeparamref name="TRow"/> is the flow's own source row shape, the
/// same one every branch's <see cref="IRowTransform{TRow,TEntity}"/> also reads from.
/// </summary>
public interface IRowRouter<in TRow>
{
    /// <summary>
    /// Returns the 0-based index into the step's branch list, evaluated in the branches'
    /// declared (SSIS EvaluationOrder) order -- first match wins. The last branch is always the
    /// default (no condition of its own), so this never fails to resolve an index.
    /// </summary>
    int SelectBranch(TRow row, in RowContext ctx);
}
