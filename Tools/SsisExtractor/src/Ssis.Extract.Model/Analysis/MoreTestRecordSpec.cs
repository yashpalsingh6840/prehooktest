using System.Text.Json.Serialization;

namespace Ssis.Extract.Model.Analysis;

/// <summary>
/// What happened to one <c>fills[-library]/&lt;Package&gt;/MoreTests/*.cs</c> file found by
/// <c>ssisx apply-tests</c>, recorded in <c>tests-applied.json</c>.
///
/// Deliberately a SMALLER taxonomy than <see cref="FillStatus"/>: a "more test" file answers no
/// gap at all (see <c>Docs/AI-Test-Enrichment-Plan.md</c>'s own Decision 2 -- this is voluntary
/// enrichment of an already-complete package, never a `gaps.json` entry), so there is no `GapId`
/// to check it against and therefore no staleness check possible -- stated as plainly here as
/// <c>LocalFileSourceData</c>'s own doc comment states the identical limitation for a raw
/// `TestData/` file. The self-check this design leans on instead is real: a "more test" file
/// references the actual generated row/entity/transform types by name, so a stale one fails to
/// COMPILE the moment the package it targets is regenerated -- an immediate, unambiguous signal.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MoreTestStatus
{
    /// <summary>Copied into <c>generate/&lt;Package&gt;.Tests/MoreTests/</c> and verified: the
    /// package's test project compiles with it in place. Every file present under
    /// <c>MoreTests/</c> that verifies is applied, whether or not it carries its own
    /// <c>// ssisx-more-test:</c> provenance comment (see <see cref="Attributed"/>).</summary>
    Applied,

    /// <summary>The compiler blamed this file, so the copy in <c>generate/</c> was removed again
    /// and the package's test project was left without it. The original under <c>fills/</c> is
    /// untouched. <see cref="MoreTestRecordSpec.CompilerErrors"/> holds the diagnostics.</summary>
    Rejected,

    /// <summary>Copied, but the test project's build failed for a reason the compiler did not
    /// attribute to any MoreTests file (a restore problem, an error in the main project, ...).
    /// The copy is left in place, because nothing here can say it is the cause.</summary>
    Unverified,
}

/// <summary>
/// One row of <c>tests-applied.json</c> -- <c>ssisx apply-tests</c>' own audit trail of every
/// file it found in <c>fills[-library]/&lt;Package&gt;/MoreTests/*.cs</c>. Written fresh on every
/// run (generated output, not hand-maintained -- the hand-maintained side is the test source
/// itself, under <c>fills/</c>, which this command only ever reads).
/// </summary>
public sealed class MoreTestRecordSpec
{
    public required string Package { get; init; }
    public required string FileName { get; init; }
    public required MoreTestStatus Status { get; init; }

    /// <summary>True when the file's own leading <c>// ssisx-more-test:</c> comment was found and
    /// parsed -- audit-only, never used to decide whether the file is applied (every file present
    /// is applied unconditionally; see <see cref="MoreTestStatus.Applied"/>'s own doc comment).
    /// A false here flags an undocumented addition for a human to notice, nothing more.</summary>
    public required bool Attributed { get; init; }

    /// <summary>From the file's own <c>// ssisx-more-test:</c> comment, when present.</summary>
    public string? Author { get; init; }

    public string? Date { get; init; }

    /// <summary>The generated class/method this test targets, from the comment's own
    /// <c>Targets=</c> field, when present.</summary>
    public string? Targets { get; init; }

    /// <summary>Set only when <see cref="Status"/> is <see cref="MoreTestStatus.Rejected"/>: the
    /// compiler diagnostics that got the file removed, one per line.</summary>
    public IReadOnlyList<string>? CompilerErrors { get; init; }
}
