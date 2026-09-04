using System.Data;
using System.Data.Common;
using Etl.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Etl.Core.Abstractions;

/// <summary>
/// Owns one DbContext and one explicit transaction that every pre-load statement (TRUNCATE ...)
/// and every bulk insert in a load shares -- so a failure partway through rolls back everything,
/// not just the half SSIS would have auto-committed.
/// </summary>
/// <remarks>
/// Deliberately does NOT expose the live SqlConnection/SqlTransaction (an earlier version did).
/// That leak forced anything consuming it -- SqlBulkSink, in particular -- to be tested only
/// against a real SQL Server, because there was no way to fake "a connection and transaction"
/// without standing one up. <see cref="BulkInsertAsync"/> is the seam instead: a fake
/// <see cref="IUnitOfWork"/> can implement it by just recording what it was called with, so
/// SqlBulkSink is unit-testable with no database at all.
/// </remarks>
public interface IUnitOfWork : IAsyncDisposable
{
    DbContext Context { get; }

    Task BeginAsync(IsolationLevel level, CancellationToken ct);

    /// <summary>Runs a pre-load statement (TRUNCATE TABLE ...) inside the active transaction.</summary>
    Task<int> ExecuteSqlAsync(string sql, CancellationToken ct);

    /// <summary>
    /// Runs a parameterized SQL statement (an OLE DB Command's per-row execution) inside the
    /// active transaction. <paramref name="sql"/> uses EF Core's own "{0}", "{1}", ... raw-SQL
    /// placeholder convention -- EF Core converts these to real DbParameters, so this stays
    /// injection-safe with no new ADO.NET plumbing. A null entry in <paramref name="parameters"/>
    /// is passed through as DBNull.Value.
    /// </summary>
    Task<int> ExecuteSqlAsync(string sql, object?[] parameters, CancellationToken ct);

    /// <summary>
    /// An OLE DB Destination in fast-load mode. Implemented for real via SqlBulkCopy enlisted in
    /// this unit of work's own connection/transaction; returns the row count actually written.
    /// </summary>
    Task<long> BulkInsertAsync(
        string destinationTable,
        DbDataReader reader,
        IReadOnlyList<BulkColumnMapping> columnMappings,
        BulkCopyOptions options,
        CancellationToken ct);

    /// <summary>
    /// Runs a statement with NO ambient transaction, so it commits on its own -- the position an
    /// SSIS Failure-precedence-constraint successor occupies (see
    /// <see cref="FailureHandlerAction"/>). Valid only AFTER <see cref="RollbackAsync"/>:
    /// implementations must reject a call made while a transaction is still active, so a handler
    /// can never silently enlist in a transaction that is about to be discarded.
    /// </summary>
    /// <remarks>
    /// That this works at all was measured, not assumed: after EF Core's own
    /// <c>RollbackAsync</c>, <c>DbContext.Database.CurrentTransaction</c> is null and a subsequent
    /// raw command succeeds and PERSISTS, while the rolled-back one does not. Hence no second
    /// connection, scope factory, or connection-string handling is needed here. The explicit
    /// no-active-transaction check exists so that if a future EF version stopped clearing the
    /// transaction, this fails loudly instead of quietly writing nothing.
    /// </remarks>
    Task<int> ExecuteSqlWithoutTransactionAsync(string sql, CancellationToken ct);

    /// <summary>
    /// A SQL Server bind token (<c>sp_getbindtoken</c>) for this unit of work's own active
    /// transaction -- lets a SEPARATE connection (<see cref="Etl.Core.Data.SqlRowSource{TRow}"/>'s
    /// own, deliberately independent one) join that transaction's lock space via
    /// <c>sp_bindsession</c>, so it sees this transaction's OWN uncommitted writes instead of
    /// blocking on their locks.
    /// </summary>
    /// <remarks>
    /// Exists because binding is the only fix for a real deadlock a plain isolation-level change
    /// cannot reach: a pre-load <c>TRUNCATE TABLE</c> (run inside this same transaction) holds a
    /// schema-stability (Sch-M) lock on that table for the transaction's ENTIRE remaining
    /// lifetime -- and Sch-M blocks even a READ UNCOMMITTED/NOLOCK reader on another connection,
    /// since NOLOCK only skips row/page locks, never schema-stability ones. Measured directly,
    /// not assumed: a real generated run with two flows -- one bulk-inserting a table an earlier
    /// pre-load statement had just TRUNCATEd, the next flow's own <c>SqlRowSource</c> joining
    /// that same table on a separate connection -- deadlocked under READ COMMITTED and STILL
    /// deadlocked after switching that reader to READ UNCOMMITTED; a live probe against the same
    /// container confirmed a plain DELETE's row locks ARE bypassed by READ UNCOMMITTED while an
    /// open TRUNCATE's own Sch-M lock is not, isolating TRUNCATE specifically as the blocker.
    /// <c>sp_bindsession</c> is the SQL Server feature built for exactly this "second physical
    /// connection, same logical transaction" shape: once bound, a follower session shares the
    /// writer's transaction outright (no isolation-level trick needed, no dirty-read caveat --
    /// it sees exactly what the writer itself would see, because it IS the writer's transaction),
    /// and a live probe confirmed the token is reusable across any number of separate bound
    /// connections, each seeing the writer's current state including writes made after the token
    /// was fetched. The one alternative that would have avoided all this -- running pre-load
    /// statements outside the load transaction, auto-committed, the way SSIS itself runs each
    /// task -- was rejected: a generated <c>Program.cs</c>'s own whole-transaction design
    /// (TRUNCATE included) is a deliberate, already-shipped improvement over SSIS's separate
    /// auto-commit, not something to undo to work around a self-inflicted deadlock.
    /// </remarks>
    Task<string> GetBindTokenAsync(CancellationToken ct);

    Task CommitAsync(CancellationToken ct);

    Task RollbackAsync(CancellationToken ct);
}
