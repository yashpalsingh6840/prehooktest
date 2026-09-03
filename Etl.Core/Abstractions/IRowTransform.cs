namespace Etl.Core.Abstractions;

/// <summary>
/// A Derived Column transform. Pure and row-at-a-time. <typeparamref name="TRow"/> (the CSV/pipeline
/// buffer shape) is deliberately a different type from <typeparamref name="TEntity"/> (the destination
/// table shape) -- SSIS's pipeline buffer is not the destination table, and columns like FirstName/
/// LastName/City/State exist only to compute derived columns, never reaching the target table.
/// </summary>
public interface IRowTransform<in TRow, out TEntity>
{
    TEntity Map(TRow row, in RowContext ctx);
}
