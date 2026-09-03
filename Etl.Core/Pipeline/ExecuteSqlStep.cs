using System.Diagnostics;
using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Pipeline;

/// <summary>
/// An Execute SQL Task positioned AFTER at least one Data Flow Task -- runs inside the same
/// transaction as every other step (PackageRunner's own uow), unlike a pre-load statement,
/// which PackageRunner runs from IEtlPackage.PreLoadStatements before any step at all. Row
/// count is reported as written, not read -- there is nothing upstream to read from.
/// </summary>
public sealed class ExecuteSqlStep(string name, string sql, ILogger<ExecuteSqlStep> logger) : ILoadTask
{
    public string Name => name;

    public async Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("{Step}: executing {Sql}", name, sql);
        var rowsAffected = await uow.ExecuteSqlAsync(sql, ct);
        return new StepResult(name, 0, rowsAffected, stopwatch.Elapsed);
    }
}
