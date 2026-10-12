using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Svk.Core;

/// <summary>
/// Phase 5.2 (G1) of the independent-review fix plan: closes a <see cref="GapKind.LocalFileSourceData"/>
/// gap automatically from svk's own already-verified sample data, instead of always requiring a
/// human/AI to hand-supply one. svk's own CSV/fixed-width/Excel writers (<see cref="SampleDataWriter"/>)
/// already match every real LOCAL-DATA work packet's schema exactly (columns, CRLF, header, fixed
/// widths, a real .xlsx with the right worksheet name) -- the only mismatch is the FILE NAME
/// (svk names its output by SSIS COMPONENT, a gap wants the connection manager's own configured
/// <c>SourceFileName</c>, see <see cref="GapSpec.ExpectedFileName"/>'s own doc comment). This
/// resolves that one mismatch and copies the file into the fills directory
/// <c>ssisx apply-fills</c> already reads, tagged Synthetic via a <c>.provenance</c> sidecar so a
/// human can tell it apart from a hand-supplied, reviewed sample later.
///
/// <b>Governance:</b> this file references only <c>Ssis.Extract.Model</c>/<c>Ssis.Extract.Dtsx</c>
/// (see <c>SvkCore.csproj</c>'s own comment) -- it never calls into <c>Ssis.Extract.Codegen</c>/
/// <c>Cli</c>. Wiring to <c>ssisx apply-fills</c> is filesystem-only: this writes plain files
/// under <c>fills/&lt;Package&gt;/TestData/</c>, the exact shape <c>ApplyFillsCommand</c> already
/// scans, and never touches <c>gaps.json</c>/decisions/anything else that command owns.
/// </summary>
public static class GapFillWriter
{
    public sealed record GapFillOutcome(string GapId, string Message, bool Written);

    /// <summary>
    /// The same three-way resolution <c>Ssis.Extract.Codegen.AiPacketEmitter.
    /// LocalFileSourceDataExpectedFileName</c>'s own component-lookup half uses, reimplemented
    /// here rather than shared (Codegen is off limits per the governance comment above): decides
    /// which SSIS pipeline component a <see cref="GapKind.LocalFileSourceData"/> gap's evidence
    /// actually points at, so its already-written svk sample file can be found by name.
    /// - CSV/fixed-width: <see cref="GapSpec.EvidenceRefId"/> is the connection manager's own
    ///   RefId; the source component is whichever one's <c>FlatFileSource</c>/<c>ExcelSource</c>
    ///   references that connection manager by name.
    /// - Excel/XML: no connection-manager RefId is recorded; <see cref="GapSpec.Location"/> IS
    ///   the source component's own name already.
    /// </summary>
    public static string? ResolveSourceComponentName(PackageSpec package, GapSpec gap)
    {
        if (gap.EvidenceRefId is { Length: > 0 } refId)
        {
            var cm = package.ConnectionManagers.FirstOrDefault(c => c.RefId == refId);
            if (cm is null) return null;

            return PackageTree.AllExecutables(package)
                .Select(e => e.DataFlowTask?.Pipeline)
                .Where(p => p is not null)
                .SelectMany(p => p!.Components)
                .FirstOrDefault(c => c.FlatFileSource?.ConnectionName == cm.ObjectName
                                   || c.ExcelSource?.ConnectionName == cm.ObjectName)
                ?.Name;
        }

        return gap.Location;
    }

    /// <summary>
    /// Copies svk's own already-written sample (under <paramref name="sampleDir"/> -- one
    /// package's own <c>sampledata/&lt;Package&gt;/</c> output from the SAME run) into
    /// <c>&lt;fillsDir&gt;/&lt;Package&gt;/TestData/&lt;ExpectedFileName&gt;</c> for every
    /// <see cref="GapKind.LocalFileSourceData"/> gap this package owns, when a matching svk
    /// sample exists. <b>Never overwrites a file already there</b> -- a human/AI answer, or a
    /// prior synthetic one, always wins; this only ever fills a gap nobody has touched yet.
    /// </summary>
    public static List<GapFillOutcome> WriteForGaps(PackageSpec package, IReadOnlyList<GapSpec> gaps, string sampleDir, string fillsDir, int seed)
    {
        var outcomes = new List<GapFillOutcome>();
        // The three real file kinds SampleDataWriter.WriteSource ever produces for a flat-file-
        // or Excel-shaped source (see its own dispatch) -- a SQL-shaped source's own ".sql"
        // script is deliberately NOT a candidate here, since a LocalFileSourceData gap only ever
        // exists for a CSV/fixed-width/Excel/XML connection manager in the first place.
        var candidateExtensions = new[] { ".csv", ".txt", ".xlsx" };

        foreach (var gap in gaps.Where(g => g.Package == package.ObjectName
                                          && g.Kind == GapKind.LocalFileSourceData
                                          && g.ExpectedFileName is { Length: > 0 }))
        {
            var componentName = ResolveSourceComponentName(package, gap);
            if (componentName is null)
            {
                outcomes.Add(new GapFillOutcome(gap.GapId, "could not resolve a source component for this gap -- not auto-closed", false));
                continue;
            }

            var safeName = Naming.SafeFileName(componentName);
            var sourceFile = candidateExtensions
                .Select(ext => Path.Combine(sampleDir, safeName + ext))
                .FirstOrDefault(File.Exists);
            if (sourceFile is null)
            {
                outcomes.Add(new GapFillOutcome(gap.GapId, $"no svk sample found for component '{componentName}' -- not auto-closed", false));
                continue;
            }

            var destDir = Path.Combine(fillsDir, package.ObjectName, "TestData");
            Directory.CreateDirectory(destDir);
            var destFile = Path.Combine(destDir, gap.ExpectedFileName!);
            if (File.Exists(destFile))
            {
                outcomes.Add(new GapFillOutcome(gap.GapId, $"TestData/{gap.ExpectedFileName} already exists -- not overwritten", false));
                continue;
            }

            File.Copy(sourceFile, destFile);
            // A raw data file has no comment syntax to carry provenance in (the same reason
            // ApplyFillsCommand's own doc comment gives for skipping a staleness check on
            // TestData/ entirely) -- a sidecar is the only place left to record this was
            // synthesized, not hand-supplied.
            File.WriteAllText(destFile + ".provenance", $"Synthetic: svk sampledata seed={seed}\n");
            outcomes.Add(new GapFillOutcome(gap.GapId, $"wrote TestData/{gap.ExpectedFileName} (synthetic, from {Path.GetFileName(sourceFile)})", true));
        }

        return outcomes;
    }
}
