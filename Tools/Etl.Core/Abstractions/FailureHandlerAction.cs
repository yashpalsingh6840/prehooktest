namespace Etl.Core.Abstractions;

/// <summary>
/// One SSIS Failure-precedence-constraint successor: a statement that runs ONLY when the package
/// has already failed (typically writing an error-log row).
///
/// <para><b>Why this is its own list rather than an <see cref="ILoadTask"/> in the generated
/// step list itself.</b> A failure handler runs after the package transaction has been
/// ROLLED BACK, and its own work must still be committed -- both measured against real SSIS
/// (dtexec, SyntheticCondConstraint.dtsx): the handler ran, its INSERT persisted, and the package
/// still returned DTSER_FAILURE. Nothing in the ordinary step list can express that: every step
/// runs inside the transaction, and by the time one is known to have failed the transaction is
/// gone. So this is a distinct position a generated <c>Program.cs</c>'s own catch block reaches
/// only after rolling back, not a step with a condition attached.</para>
///
/// <para>Deliberately plain data (a name and SQL text) rather than an interface, matching
/// <see cref="FileSystemPreLoadAction"/>: the one evidenced handler in the tracked portfolio is a
/// single terminal Execute SQL Task, and inventing a richer shape would be guessing at a
/// capability no real package asks for.</para>
/// </summary>
/// <param name="TaskName">The SSIS task's own name, for logging and for
/// <see cref="PackageResult.FailureHandlersRun"/>.</param>
/// <param name="Sql">The statement, verbatim from the .dtsx.</param>
public sealed record FailureHandlerAction(string TaskName, string Sql);
