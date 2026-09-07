namespace Etl.Core.Abstractions;

/// <summary>One Data Flow Task: Source -> Transform -> Sink, run inside the package's UnitOfWork.</summary>
public interface ILoadTask
{
    string Name { get; }

    Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct);
}

/// <summary>Run identity shared by every step in one package execution.</summary>
public sealed record LoadContext(Guid RunId, DateTime StartedAtUtc, string PackageName);

/// <summary>Row counts + timing for one data flow.</summary>
public sealed record StepResult(string Name, long RowsRead, long RowsWritten, TimeSpan Elapsed)
{
    /// <summary>True when the step did not run at all because a conditional precedence
    /// constraint gating it evaluated false -- a generated <c>Program.cs</c> wraps such a step's
    /// own <c>RunAsync</c> call in an inline guard and reports this instead of ever invoking it.
    /// Kept distinct from a step that ran and moved zero rows: those are different facts, and
    /// (0, 0) alone cannot tell them apart. Defaults false, so every existing construction site
    /// -- generated or hand-written -- is unchanged.</summary>
    public bool Skipped { get; init; }
}

/// <summary>Whole-run outcome; maps directly to the process exit code.</summary>
public sealed record PackageResult(
    string Name,
    bool Succeeded,
    IReadOnlyList<StepResult> Steps,
    TimeSpan Elapsed,
    Exception? Error)
{
    /// <summary>Names of the <see cref="FailureHandlerAction"/>s that actually ran, in order.
    /// Empty on a successful run. Surfaced on the result rather than only logged so a notifier can
    /// say "it failed AND the failure was logged" -- which is the whole point of a handler.</summary>
    public IReadOnlyList<string> FailureHandlersRun { get; init; } = [];
}
