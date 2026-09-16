namespace Etl.Core.Abstractions;

/// <summary>
/// One <c>Microsoft.SCD</c> output's downstream chain. Deliberately a DIFFERENT interface from
/// <see cref="IConditionalSplitBranch{TRow}"/> rather than a reuse of it, for one reason: a
/// Conditional Split/Multicast branch has a single "flush" phase, while an SCD branch may need TWO
/// separately-ordered phases, because a dimension load routinely does both kinds of work.
///
/// <para>The real evidenced package (<c>ETL-SSIS-Real-Scenarios/SCD SSIS</c>'s own <c>SCD.dtsx</c>)
/// has exactly this shape: its <c>Changing Attribute Updates Output</c> runs a per-row
/// <c>UPDATE ... SET [LastName] = ? WHERE [EmpId] = ? AND [EndDate] IS NULL</c> and nothing else,
/// while its <c>Historical Attribute Inserts Output</c> runs a per-row
/// <c>UPDATE ... SET [EndDate] = ? WHERE [EmpId] = ? AND [EndDate] IS NULL</c> (closing the old
/// row) and THEN feeds the same shared destination its <c>New Output</c> feeds (inserting the new
/// one).</para>
///
/// <para><b>Why the split matters, concretely:</b> that historical <c>UPDATE</c>'s own
/// <c>WHERE ... AND [EndDate] IS NULL</c> would also match a row inserted by this very load if the
/// insert ran first. Running every branch's commands (phase 1) before any branch's inserts (phase 2)
/// is what makes "close out the old rows, then insert the new ones" hold regardless of branch order
/// -- see <c>SlowlyChangingDimensionStep{TRow,TKey}</c>'s own doc comment.</para>
///
/// <para>The INSERT-shaped half is still genuinely reused, not reimplemented: <c>ScdBranch{TRow}</c>
/// delegates its whole buffer-and-bulk-insert path to an <see cref="IConditionalSplitBranch{TRow}"/>
/// -- the same <c>ConditionalSplitBranch{TRow,TEntity}</c> Conditional Split, Multicast and
/// Percentage Sampling already use.</para>
/// </summary>
public interface IScdBranch<in TRow>
{
    string Name { get; }

    /// <summary>Buffers one routed row. Never touches the database itself.</summary>
    void Add(TRow row, in RowContext ctx);

    /// <summary>Phase 1: runs this branch's own per-row SQL command, if it has one. Returns the
    /// number of rows affected. A branch with no command does nothing and returns 0.</summary>
    Task<long> ExecuteCommandsAsync(IUnitOfWork uow, CancellationToken ct);

    /// <summary>Phase 2: bulk-inserts this branch's own buffered rows, if it has a sink. Returns
    /// the row count written. A branch with no sink does nothing and returns 0.</summary>
    Task<long> FlushAsync(IUnitOfWork uow, CancellationToken ct);
}
