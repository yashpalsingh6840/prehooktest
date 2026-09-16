using System.Diagnostics;
using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Pipeline;

/// <summary>
/// <c>STOCK:FORLOOP</c> (For Loop Container) whose body is one Data Flow Task, re-run for as
/// long as <paramref name="eval"/> holds -- Phase 3 of the unsupported-component-types plan, the
/// counter-driven sibling of <see cref="ForEachFileDataFlowStep{TRow,TEntity}"/> (which iterates
/// over enumerated files instead). Runs <paramref name="init"/> once (skipped entirely when the
/// container declared no <c>InitExpression</c> -- the counter's own design-time default is used
/// as-is, matching real SSIS), then evaluates/runs/assigns in a plain
/// <c>while (eval()) {{ ...; assign(); }}</c> loop -- the same shape SSIS's own For Loop
/// Container uses (Init once, then Eval-Run-Assign repeating).
///
/// <para><paramref name="init"/>/<paramref name="eval"/>/<paramref name="assign"/> are all plain,
/// PARAMETERLESS delegates, the same reasoning <see cref="ExpressionTaskStep"/>'s own doc comment
/// gives for its own <c>Action</c>: the generated construction site is a method on the SAME
/// generated package class that already declares the <c>packageVariables</c> field, so each
/// delegate simply closes over it directly (e.g. <c>() =&gt; packageVariables.GetRequired&lt;int&gt;
/// ("User::Part") &lt; 11</c>) rather than this type threading <c>PackageVariables</c> through on
/// their behalf.</para>
///
/// <para><paramref name="sourceFactory"/> is likewise parameterless and constructs a FRESH
/// <see cref="IRowSource{TRow}"/> every iteration -- its own generated body reads the counter's
/// CURRENT value directly off <c>packageVariables</c> (the same closure, not a value snapshotted
/// once), so a per-iteration file path genuinely reflects that iteration's own counter value. Runs
/// through the SAME <paramref name="transform"/>/<paramref name="sink"/> every iteration uses (both
/// stateless/reusable) via a fresh <see cref="DataFlowStep{TRow,TEntity}"/> instance per iteration,
/// reusing that type's own streaming Source-&gt;Transform-&gt;Sink loop and its own "zero rows read
/// is an error" guard rather than duplicating either. Every iteration runs inside the SAME
/// transaction as every other step -- one failing iteration rolls back every prior one and every
/// other step in the package, the same all-or-nothing semantics <see cref="ForEachFileDataFlowStep{TRow,TEntity}"/>
/// already has.</para>
/// </summary>
public sealed class ForLoopStep<TRow, TEntity>(
    string name,
    Action? init,
    Func<bool> eval,
    Action assign,
    Func<IRowSource<TRow>> sourceFactory,
    IRowTransform<TRow, TEntity> transform,
    IBulkSink<TEntity> sink,
    ILogger<DataFlowStep<TRow, TEntity>> stepLogger) : ILoadTask
    where TEntity : class
{
    public string Name => name;

    public async Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        init?.Invoke();

        long totalRead = 0, totalWritten = 0;
        var iteration = 0;
        while (eval())
        {
            ct.ThrowIfCancellationRequested();

            var source = sourceFactory();
            var iterationStep = new DataFlowStep<TRow, TEntity>($"{name}[{iteration}]", source, transform, sink, stepLogger);
            var result = await iterationStep.RunAsync(uow, load, ct);
            totalRead += result.RowsRead;
            totalWritten += result.RowsWritten;

            assign();
            iteration++;
        }

        return new StepResult(name, totalRead, totalWritten, stopwatch.Elapsed);
    }
}
