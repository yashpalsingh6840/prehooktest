namespace Etl.Core.Abstractions;

/// <summary>One Data Flow Task: Source -> Transform -> Sink, run inside the package's UnitOfWork.</summary>
public interface ILoadTask
{
    string Name { get; }

    Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct);
}

/// <summary>One .dtsx package's Control Flow: pre-load SQL, then pre-load file actions, then
/// steps run strictly in order.</summary>
public interface IEtlPackage
{
    string Name { get; }

    /// <summary>The Execute SQL Task(s) that run before any data flow -- e.g. TRUNCATE TABLE ...</summary>
    IReadOnlyList<string> PreLoadStatements { get; }

    /// <summary>File System Task(s) that run before any data flow, after every
    /// <see cref="PreLoadStatements"/> entry -- see <see cref="FileSystemPreLoadAction"/>'s own
    /// doc comment for why this is a separate list rather than folded into
    /// <see cref="PreLoadStatements"/> or <see cref="Steps"/>. Defaults to empty so every
    /// existing <see cref="IEtlPackage"/> implementer (hand-written or generated) that has no
    /// File System Task needs no change at all.</summary>
    IReadOnlyList<FileSystemPreLoadAction> PreLoadFileActions => [];

    /// <summary>Data flows, in the exact order the on-success precedence constraints require.</summary>
    IReadOnlyList<ILoadTask> Steps { get; }

    /// <summary>Statements that run ONLY after the package has failed and its transaction has been
    /// rolled back, each committing on its own -- see <see cref="FailureHandlerAction"/> for why
    /// this cannot be a step. Defaults to empty, so every existing implementer with no Failure
    /// precedence constraint needs no change at all.</summary>
    IReadOnlyList<FailureHandlerAction> FailureHandlers => [];
}

/// <summary>Run identity shared by every step in one package execution.</summary>
public sealed record LoadContext(Guid RunId, DateTime StartedAtUtc, string PackageName);

/// <summary>Row counts + timing for one data flow.</summary>
public sealed record StepResult(string Name, long RowsRead, long RowsWritten, TimeSpan Elapsed)
{
    /// <summary>True when the step did not run at all because a conditional precedence
    /// constraint gating it evaluated false -- see <see cref="Etl.Core.Pipeline.ConditionalStep"/>.
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
