using System.Diagnostics;
using Etl.Core.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Pipeline;

/// <summary>
/// An Execute SQL Task whose own connection manager is NOT the package's primary one -- e.g.
/// RBC_Demo_ETL's own <c>SQL_CacheSet_SecondDb</c>, which calls <c>dbo.Cache_Set</c> in a
/// second database, <c>SSISDemoCache</c>, while every other task in the package targets
/// <c>SSISDemo</c>. Unlike <see cref="ExecuteSqlStep"/>, this deliberately does NOT use the
/// <see cref="IUnitOfWork"/> passed to <see cref="RunAsync"/> at all -- it opens its own,
/// completely independent <see cref="SqlConnection"/> and runs autocommitted, with no explicit
/// transaction of its own and no enlistment in the package's.
///
/// <para>This is the faithful translation, not a shortcut: real SSIS gives every connection
/// manager its own physical connection by default (<c>RetainSameConnection=False</c>), and two
/// different connection managers only ever share a transaction when a container's own
/// <c>TransactionOption</c> is <c>Required</c> and MSDTC escalates -- confirmed absent on the
/// real evidenced package (no <c>DTS:TransactionOption</c> anywhere in
/// <c>Package_Legacy.dtsx</c>), so SSIS itself runs this task autocommitted, uncoordinated with
/// anything else. Enlisting it in the package's own <see cref="IUnitOfWork"/> transaction
/// instead would be WRONG in the other direction: a distinct database can't join a
/// <c>SqlTransaction</c> already bound to a different physical connection without a
/// distributed transaction (MSDTC) -- a design this rewrite has deliberately not taken on
/// elsewhere (see <see cref="PackageRunner"/>'s own remarks on parallel execution) -- and even
/// if it could, that would make this task's own success or failure roll back a database SSIS
/// itself never coordinated with the load at all.</para>
///
/// <para>Same independent-connection precedent as <see cref="Etl.Core.Data.SqlRowSource{TRow}"/>
/// already established on the read side; this is that idea applied to a write.</para>
/// </summary>
public sealed class SecondaryConnectionSqlStep(
    string name,
    string connectionManagerName,
    string connectionString,
    string sql,
    ILogger<SecondaryConnectionSqlStep> logger) : ILoadTask
{
    public string Name => name;

    public async Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "{Step}: executing {Sql} on '{ConnectionManager}' (its own connection, outside the package transaction)",
            name, sql, connectionManagerName);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rowsAffected = await command.ExecuteNonQueryAsync(ct);

        return new StepResult(name, 0, rowsAffected, stopwatch.Elapsed);
    }
}
