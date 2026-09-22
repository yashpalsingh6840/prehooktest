using System.Diagnostics;
using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Pipeline;

/// <summary>
/// <c>Microsoft.ExpressionTask</c> -- one control-flow-level assignment into the package's own
/// <see cref="PackageVariables"/> bag (e.g. <c>@[User::TargetETLCutoffTime] = DATEADD("Minute",
/// -5,GETUTCDATE())</c>). Runs as an ordinary step at its own real topological position, same as
/// <see cref="ExecuteSqlStep"/>/<see cref="FileSystemStep"/> -- there is no separate pre-load
/// position for this task type, since a design-time-only assignment has no meaning ahead of the
/// generated step list the way a pre-load SQL statement or file action does. Reports zero rows
/// either side -- an assignment moves no data.
///
/// <para><paramref name="assign"/> is a plain, PARAMETERLESS <c>Action</c>, not
/// <c>Action&lt;PackageVariables&gt;</c> -- unlike a Script Task (its own generated class, taking
/// <c>PackageVariables</c> explicitly as a constructor argument since it lives in a separate
/// file), an ExpressionTask's generated construction site is a method on the SAME generated
/// package class that already declares the <c>packageVariables</c> field, so the assignment
/// lambda simply closes over it directly (e.g. <c>() =&gt; packageVariables.Set("User::X", ...)</c>)
/// -- there is nothing this type needs to thread through on its own behalf.</para>
/// </summary>
public sealed class ExpressionTaskStep(string name, Action assign, ILogger<ExpressionTaskStep> logger) : ILoadTask
{
    public string Name => name;

    public Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("{Step}: evaluating expression assignment", name);
        assign();
        return Task.FromResult(new StepResult(name, 0, 0, stopwatch.Elapsed));
    }
}
