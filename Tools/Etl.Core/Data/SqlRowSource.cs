using System.Data.Common;
using System.Runtime.CompilerServices;
using Etl.Core.Abstractions;
using Microsoft.Data.SqlClient;

namespace Etl.Core.Data;

/// <summary>
/// An OLE DB Source. Owns its own connection, entirely separate from the package's load
/// transaction (<see cref="IRowSource{TRow}.ReadAsync"/> gets no <c>IUnitOfWork</c>/connection
/// parameter to reuse) -- the same separation a real SSIS OLE DB Source's own read connection
/// has from the destination's transactional one.
///
/// A package's whole run is one long-lived transaction on a SEPARATE connection
/// (<see cref="UnitOfWork"/>'s own), held open until the package either commits or rolls back at
/// the very end -- so any later flow whose own query reads a table an EARLIER flow in the SAME
/// run already wrote is reading a table this second, independent connection cannot see as
/// COMMITTED yet. Confirmed real, not theoretical: a generated run of a genuine two-flow package
/// (the second flow's SqlRowSource joining a table the first flow had just bulk-inserted, right
/// after a pre-load TRUNCATE of that same table, all still uncommitted) deadlocked -- the read
/// blocked for the full command timeout, then failed with "Execution Timeout Expired", every
/// time, deterministically. This is not a hazard particular to that one flow -- it is inherent to
/// pairing "one open transaction for the whole package" with "every SQL source is its own,
/// separate connection".
///
/// <paramref name="uow"/>, when supplied, is used to bind this connection to the package's own
/// transaction (<see cref="IUnitOfWork.GetBindTokenAsync"/>'s own doc comment has the full
/// story: a plain isolation-level relaxation was tried first and measured NOT to work here,
/// because a pre-load TRUNCATE's schema-stability lock blocks even a READ UNCOMMITTED reader --
/// only <c>sp_bindsession</c>, joining the writer's transaction outright, does). Optional, and
/// defaulting to <see langword="null"/>, so every pre-existing call site (a Lookup preload, any
/// read that genuinely has no package transaction yet to bind to) compiles and behaves exactly
/// as before -- unbound, it falls back to READ UNCOMMITTED, which is still correct (a no-op) for
/// any read that isn't racing a same-run write, just not sufficient for the TRUNCATE case.
///
/// This call to <see cref="IUnitOfWork.GetBindTokenAsync"/> MUST hit that method's own cache,
/// never issue a fresh command on the writer's connection -- <see cref="Etl.Core.Pipeline.PackageRunner"/>
/// pre-fetches the token once, right after the transaction begins, specifically so it is already
/// cached by the time this runs. A real, reproduced deadlock is why: a SqlRowSource feeding a
/// SqlBulkSink destination is read from WHILE that destination's own SqlBulkCopy is actively
/// streaming on the writer's connection (pulling rows as it writes), so fetching the token HERE,
/// lazily, would issue a second command on a connection SqlBulkCopy is already mid-operation on --
/// which can never complete, since a plain ADO.NET connection has no way to service two commands
/// at once without MARS.
/// </summary>
public sealed class SqlRowSource<TRow>(
    string name, SqlSourceOptions options, Func<DbDataReader, TRow> materialize, IUnitOfWork? uow = null) : IRowSource<TRow>
{
    public string Name => name;

    public async IAsyncEnumerable<TRow> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(ct);

        if (uow is not null)
        {
            var token = await uow.GetBindTokenAsync(ct);
            await using var bindCommand = connection.CreateCommand();
            bindCommand.CommandText = $"EXEC sp_bindsession '{token.Replace("'", "''")}'";
            await bindCommand.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var isolationCommand = connection.CreateCommand();
            isolationCommand.CommandText = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;";
            await isolationCommand.ExecuteNonQueryAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = options.CommandText;
        command.CommandTimeout = options.CommandTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return materialize(reader);
    }
}
