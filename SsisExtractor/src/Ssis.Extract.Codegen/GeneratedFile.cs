using Ssis.Extract.Model.Analysis;

namespace Ssis.Extract.Codegen;

/// <summary>One file an emitter produced. RelativePath is relative to `ssisx generate`'s
/// --out directory, e.g. "Model/Employee.cs". Content always ends in exactly one trailing
/// "\n" and uses LF only -- see <see cref="Rendering.JoinLines"/>, the one place every
/// emitter builds its output text, so this holds without each emitter re-deriving it.</summary>
public sealed record GeneratedFile(string RelativePath, string Content);

/// <summary>
/// One column, table, or expression an emitter could not generate for -- or, when
/// <see cref="IsBlocking"/> is false, an advisory the caller should read before running
/// generated code that WAS produced anyway. Surfaced in EmitResult rather than silently
/// dropped or guessed -- the same "a gap must be visible" rule
/// <c>PipelineResolver.ResolveDestinationInput</c> and the non-determinism manifest already
/// follow. <see cref="Location"/> is a human-readable path like "Employee.Salary";
/// <see cref="Reason"/> says exactly why generation stopped there (or what to verify).
/// <see cref="IsBlocking"/> defaults to true; set it false only where generation genuinely
/// produced complete, wired output and the gap is informational -- e.g. the unavoidable
/// per-package Notification gap, or the SqlCommand-mode column-name assumption in
/// <c>PackageGenerator.BuildSqlFlowSource</c>. Consumers should key off this flag rather than
/// pattern-matching <see cref="Location"/>/<see cref="Reason"/> text.
/// </summary>
public sealed record GenerationGap(
    string Location,
    string Reason,
    bool IsBlocking = true,
    // Both trailing parameters are OPTIONAL specifically so all ~143 existing
    // `new GenerationGap(...)` call sites in this assembly compile unchanged -- only the handful
    // whose gaps map to a real AI-assisted workflow (Script Task, Script Component column,
    // Lookup join key) set them. See GapKind's own doc comment for why classification is a
    // separate axis from IsBlocking.
    GapKind Kind = GapKind.Unclassified,
    // The refId of the object carrying this gap's EVIDENCE (an ExecutableSpec for a Script
    // Task, a PipelineComponentSpec for a Script Component/Lookup) -- carried structurally rather
    // than parsed back out of Reason text, so AiPacketEmitter can resolve the real source/SQL/
    // column list unambiguously even when two components share a display name.
    string? EvidenceRefId = null);

/// <summary>What one emitter call produced: zero or more files, plus every gap hit along the
/// way. A gap and a file are not mutually exclusive -- an entity with 7 of 8 columns resolved
/// still emits a file for those 7 and reports the 8th as a gap.</summary>
public sealed record EmitResult(List<GeneratedFile> Files, List<GenerationGap> Gaps);

/// <summary>
/// The one place source text gets turned into a <see cref="GeneratedFile.Content"/> string,
/// shared by every emitter so the LF-only, single-trailing-newline rule can't drift between
/// them. Deliberately NOT a raw multi-line string literal anywhere in this project -- the
/// generate plan calls out TestGenCommand mixing CRLF (raw literals), LF (string.Join) and
/// Environment.NewLine (AppendLine) as the exact bug a byte-exact golden test would catch.
/// </summary>
internal static class Rendering
{
    public static string JoinLines(IEnumerable<string> lines) => string.Join('\n', lines) + "\n";
}
