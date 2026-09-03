namespace Ssis.Validate.Harness;

/// <summary>
/// One captured golden run: the exact inputs and outputs of a real package execution,
/// self-contained under <c>corpus/&lt;Package&gt;/&lt;RunId&gt;/</c> (plan §12) so replaying it later
/// never depends on <c>Data/Input/</c> or SSISDB still holding the same state.
/// </summary>
public sealed class CorpusManifest
{
    public required string PackageName { get; init; }
    public required long RunId { get; init; }
    public required long? ExecutionId { get; init; }
    public required string CapturedAtUtc { get; init; }
    public Dictionary<string, string> Parameters { get; init; } = [];

    /// <summary>Relative to this manifest's own directory -- copied at capture time so the corpus is self-contained.</summary>
    public List<string> InputFiles { get; init; } = [];

    public List<StageRowCount> StageRowCounts { get; init; } = [];
}

/// <summary>One row of <c>catalog.execution_data_statistics</c> (plan §5's "assert per-stage row counts") -- captured for informational/audit purposes; asserting against it requires the job under test to report its own per-stage counts in a comparable shape, which no rewrite exists yet to define (see docs/gate3-harness.md's honest-limits section).</summary>
public sealed class StageRowCount
{
    public required string TaskName { get; init; }
    public string? SourceComponentName { get; init; }
    public string? DestinationComponentName { get; init; }
    public required long RowsSent { get; init; }
}
