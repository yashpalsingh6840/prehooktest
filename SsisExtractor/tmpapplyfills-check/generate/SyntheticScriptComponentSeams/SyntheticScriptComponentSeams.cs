using System.Data;
using System.Diagnostics;
using Etl.Core.Abstractions;
using Etl.Core.Data;
using Etl.Core.Hosting;
using Etl.Core.Pipeline;
using SyntheticScriptComponentSeams.Sql;
using SyntheticScriptComponentSeams.Mapping;
using SyntheticScriptComponentSeams.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SyntheticScriptComponentSeams;

/// <summary>Ported from SSIS package 'SyntheticScriptComponentSeams'.</summary>
internal sealed partial class SyntheticScriptComponentSeamsPackage(IServiceProvider services)
{
    private const string PackageName = "SyntheticScriptComponentSeams";
    private readonly PackageVariables packageVariables = new();
    private readonly List<StepResult> _results = [];
    private LoadContext _load = null!;

    internal ILogger<T> Log<T>() => services.GetRequiredService<ILogger<T>>();
    internal IOptions<T> Opt<T>() where T : class => services.GetRequiredService<IOptions<T>>();
    private DatabaseOptions Db() => Opt<DatabaseOptions>().Value;
    private string File(string key) => Opt<FileSourceOptions>().Value[key].ResolvedPath;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var uow = sp.GetRequiredService<IUnitOfWork>();
        var logger = Log<SyntheticScriptComponentSeamsPackage>();

        var startedAtUtc = sp.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        startedAtUtc = new DateTime(startedAtUtc.Ticks - (startedAtUtc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
        _load = new LoadContext(Guid.NewGuid(), startedAtUtc, PackageName);
        var stopwatch = Stopwatch.StartNew();

        logger.LogInformation("Starting {Package}...", PackageName);
        var uowActive = false;
        try
        {
            await uow.BeginAsync(IsolationLevel.ReadCommitted, ct);
            uowActive = true;

            // Fetched once, here, while this transaction's connection is guaranteed idle -- a lazy fetch
            // from inside a step reproduced a real deadlock (SqlRowSource reading while SqlBulkCopy streams).
            await uow.GetBindTokenAsync(ct);

            await DFT_Load(uow, ct);

            await uow.CommitAsync(ct);
            logger.LogInformation("{Package}: succeeded in {Ms} ms", PackageName, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            if (uowActive)
            {
                await uow.RollbackAsync(CancellationToken.None);
            }
            logger.LogError(ex, "{Package} failed after {Steps} step(s); transaction rolled back", PackageName, _results.Count);

            return ExitCode.LoadFailed;
        }

        foreach (var step in _results) logger.LogInformation("{Step}: {Rows:N0} rows loaded", step.Name, step.RowsWritten);
        return ExitCode.Success;
    }

    internal async Task DFT_Load(IUnitOfWork uow, CancellationToken ct)
    {
        var source = OLEDBSource(uow);
        var transform = new SyntheticScriptSeamsTargetTransform();
        var sink = OLEDBDestination();

        var step = new DataFlowStep<SyntheticScriptSeamsTargetSqlRow, SyntheticScriptSeamsTarget>("DFT_Load", source, transform, sink, Log<DataFlowStep<SyntheticScriptSeamsTargetSqlRow, SyntheticScriptSeamsTarget>>());
        _results.Add(await step.RunAsync(uow, _load, ct));
    }

    internal IRowSource<SyntheticScriptSeamsTargetSqlRow> OLEDBSource(IUnitOfWork uow)
    {
        return new SqlRowSource<SyntheticScriptSeamsTargetSqlRow>("OLE DB Source", new SqlSourceOptions { ConnectionString = SqlConnectionStringFactory.Build(Db()), CommandText = "SELECT ID, FirstName, LastName FROM dbo.SyntheticScriptSeamsInput" }, SyntheticScriptSeamsTargetSqlRowReader.Read, uow);
    }

    internal IBulkSink<SyntheticScriptSeamsTarget> OLEDBDestination()
    {
        return new SqlBulkSink<SyntheticScriptSeamsTarget>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<SyntheticScriptSeamsTarget>>());
    }

}
