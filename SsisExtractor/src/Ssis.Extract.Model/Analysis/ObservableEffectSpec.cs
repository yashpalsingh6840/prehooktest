namespace Ssis.Extract.Model.Analysis;

/// <summary>
/// One thing a package changes in the outside world -- a row written, a file produced, a
/// procedure invoked, or a task whose effects this tool cannot characterize at all.
///
/// <para><b>Why this exists, and why it is deliberately pessimistic.</b> The gate-3 harness
/// (<c>Validation</c>) compares SQL table rows and nothing else. That was adequate for
/// the two packages it was built against, both of which end in a table -- but a package whose
/// real output is an email, an SFTP drop, or a queue message would be compared against
/// <i>nothing</i> and still report PASS. A partial check that reads as a full pass is worse
/// than no check, because it is trusted.</para>
///
/// <para>So this inventory's job is not to describe effects the tooling understands; it is to
/// enumerate <i>every</i> effect a package has, including the ones nothing can verify yet, so
/// the harness can refuse to claim success while any of them is unchecked. An effect that is
/// merely suspected still belongs here -- see <see cref="Verifiability"/>.</para>
///
/// <para><b>Reads are not effects.</b> A source CSV is an input, not something the package
/// changes, so it is excluded. Direction comes from pipeline component usage (a connection
/// manager used by a source vs. a destination), the same signal <c>DataTouchBuilder</c>
/// already resolves -- never guessed from the connection manager alone.</para>
/// </summary>
public sealed class ObservableEffectSpec
{
    public required string PackageName { get; init; }

    /// <summary>
    /// <c>SqlTable</c> | <c>File</c> | <c>StoredProcedure</c> | <c>UncharacterizedTask</c>.
    /// The last is the load-bearing one: it is what a Send Mail Task, an FTP Task, a Script
    /// Task, or any task type this extractor has no semantic model for becomes, so an
    /// unmodelled side effect surfaces as an explicit unknown rather than as silence.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>The table name, file path (or the expression producing it), procedure name, or task name.</summary>
    public required string Target { get; init; }

    /// <summary>Where in the package this effect comes from -- a refId, or a connection-manager name.</summary>
    public required string Origin { get; init; }

    /// <summary>
    /// <c>Verifiable</c> -- a gate-3 checker exists for this kind of effect today.
    /// <c>NeedsChecker</c> -- a real effect with no checker built yet; the gate must report
    /// INCOMPLETE rather than PASS while one of these is present.
    /// <c>NeedsHumanReview</c> -- this tool cannot even establish whether there IS an effect
    /// (an uncharacterized task), so a person has to look before the question can be answered.
    /// </summary>
    public required string Verifiability { get; init; }

    /// <summary>Why this was classified as it was, in a sentence a reviewer can act on.</summary>
    public required string Note { get; init; }
}
