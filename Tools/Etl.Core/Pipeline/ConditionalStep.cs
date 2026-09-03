using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Pipeline;

/// <summary>
/// One step gated by a conditional SSIS precedence constraint: run <paramref name="inner"/> only
/// when <paramref name="predicate"/> holds over the shared <see cref="PackageVariables"/>,
/// otherwise skip it and report <see cref="StepResult.Skipped"/>.
///
/// <para><b>A decorator, not a new step kind, and deliberately so.</b> Which step is gated is
/// orthogonal to what that step does -- SSIS puts the condition on the EDGE, not the task -- so
/// wrapping keeps every existing <see cref="ILoadTask"/> (data flow, Execute SQL, Script Task,
/// ForEach loop, Conditional Split, ...) gateable with no change to any of them.</para>
///
/// <para><b>What this does and does not reproduce, measured rather than assumed.</b> A real
/// dtexec run (the extractor's own SyntheticCondConstraint.dtsx probe) measured all seven edge
/// kinds. This type reproduces exactly one of them: <c>DTSPrecedenceEvalOp.ExpressionAndConstraint</c>
/// with the constraint half left at <c>DTSExecResult.Success</c> -- "run only if the predecessor
/// succeeded AND this expression is true". That is the only form whose semantics survive
/// <see cref="PackageRunner"/>'s single whole-package transaction, because it never asks for work
/// to happen AFTER a failure. The outcome-based forms (Failure, Completion) and the
/// constraint-ignoring Expression-only form were all measured to run their successor on the
/// FAILURE path, which this runner cannot do at all: it rolls the whole transaction back and
/// returns. Those stay generation gaps rather than being approximated here.</para>
///
/// <para>The raw SSIS expression text is carried purely so the skip is auditable -- a log line
/// saying which condition was evaluated is far more use than "step skipped".</para>
/// </summary>
public sealed class ConditionalStep(
    ILoadTask inner,
    string ssisCondition,
    Func<PackageVariables, bool> predicate,
    PackageVariables variables,
    ILogger<ConditionalStep> logger) : ILoadTask
{
    public string Name => inner.Name;

    public async Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct)
    {
        if (!predicate(variables))
        {
            logger.LogInformation(
                "{Step}: skipped -- its precedence constraint's condition ({Condition}) evaluated false",
                inner.Name, ssisCondition);
            return new StepResult(Name, 0, 0, TimeSpan.Zero) { Skipped = true };
        }

        logger.LogInformation(
            "{Step}: running -- its precedence constraint's condition ({Condition}) evaluated true",
            inner.Name, ssisCondition);
        return await inner.RunAsync(uow, load, ct);
    }
}
