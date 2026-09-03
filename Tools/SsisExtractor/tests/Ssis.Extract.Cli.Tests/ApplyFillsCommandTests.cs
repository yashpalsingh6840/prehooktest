using System.Text.Json;
using Ssis.Extract.Codegen;
using Ssis.Extract.Model.Analysis;

namespace Ssis.Extract.Cli.Tests;

/// <summary>
/// Covers <c>ApplyFillsCommand</c>'s chunk-4 addition: a fill MAY carry a
/// <c>// ssisx-fill: GapId=... Author=... Date=... EvidenceSha256=...</c> comment immediately above
/// the seam it answers, and that comment is what lets a later run tell a fill written against the
/// CURRENT package apart from one written against a version of it that has since changed.
///
/// Built as real filesystem integration tests (a temp <c>--out</c> directory, real
/// <c>gaps.json</c>/<c>fills/</c> files) rather than unit tests against extracted pure logic --
/// <c>ApplyFillsCommand.Run</c> has no such seam today, and this command's whole job is file I/O,
/// so faking that away would test less than actually doing it.
/// </summary>
public class ApplyFillsCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ssisx-apply-fills-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string Package = "Pkg";
    private const string GapId = "SCRIPT-COLUMN:Pkg:Foo.Bar";
    private const string CurrentHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OldHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private string WriteGaps(params GapSpec[] gaps)
    {
        // Generated output must already exist for the package, or apply-fills reports it as
        // "unknown" before ever looking at fills for it (a different, deliberate refusal).
        Directory.CreateDirectory(Path.Combine(_root, "generate", Package));
        var gapsPath = Path.Combine(_root, "gaps.json");
        File.WriteAllText(gapsPath, JsonSerializer.Serialize(gaps.ToList()));
        return gapsPath;
    }

    private string WriteFill(string fileName, string content)
    {
        var dir = Path.Combine(_root, "fills", Package);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private List<FillRecordSpec> ReadManifest() =>
        JsonSerializer.Deserialize<List<FillRecordSpec>>(File.ReadAllText(Path.Combine(_root, "fills-applied.json")))!;

    private static GapSpec ScriptComponentColumnGap(string evidenceHash) => new()
    {
        GapId = GapId, Package = Package, Kind = GapKind.ScriptComponentColumn, Tier = GapTier.MissingLogic,
        Location = "Foo.Bar", Reason = "produced by a Script Component", IsBlocking = true,
        EvidenceSha256 = evidenceHash,
    };

    private void WriteClaim(string ruleId, string status)
    {
        var dir = Path.Combine(_root, "conformance", "claims");
        Directory.CreateDirectory(dir);
        var claim = new List<object> { new { RuleId = ruleId, Status = status, ImplementedBy = (string?)null, Note = (string?)null } };
        File.WriteAllText(Path.Combine(dir, $"{Package}.claims.json"), JsonSerializer.Serialize(claim));
    }

    private static string CaptureConsole(Action action)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try { action(); }
        finally { Console.SetOut(original); }
        return writer.ToString();
    }

    [Fact]
    public void Run_AppliesAFill_WhenTheProvenanceHashMatchesTheCurrentGap()
    {
        WriteGaps(ScriptComponentColumnGap(CurrentHash));
        WriteFill("FooTransform.Fills.cs", $$"""
            namespace Pkg.Mapping;

            public sealed partial class FooTransform
            {
                // ssisx-fill: GapId={{GapId}} Author=tester Date=2026-09-03 EvidenceSha256={{CurrentHash}}
                private partial string Fill_Bar(FooRow row, in RowContext ctx) => "x";
            }
            """);

        var exitCode = ApplyFillsCommand.Run(["--out", _root]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(_root, "generate", Package, "Fills", "FooTransform.Fills.cs")));

        var record = Assert.Single(ReadManifest());
        Assert.Equal(FillStatus.Applied, record.Status);
        Assert.Equal(GapId, record.GapId);
        Assert.Equal("tester", record.Author);
        Assert.Equal("2026-09-03", record.Date);
        Assert.Equal(CurrentHash, record.RecordedEvidenceSha256);
        Assert.Equal(CurrentHash, record.CurrentEvidenceSha256);
    }

    [Fact]
    public void Run_RefusesAStaleFill_AndDoesNotCopyIt()
    {
        WriteGaps(ScriptComponentColumnGap(CurrentHash));
        WriteFill("FooTransform.Fills.cs", $$"""
            namespace Pkg.Mapping;

            public sealed partial class FooTransform
            {
                // ssisx-fill: GapId={{GapId}} Author=tester Date=2026-08-01 EvidenceSha256={{OldHash}}
                private partial string Fill_Bar(FooRow row, in RowContext ctx) => "x";
            }
            """);

        var exitCode = ApplyFillsCommand.Run(["--out", _root]);

        // Not orphaned/unknown (1) -- the gap is real -- but genuinely incomplete: the seam is
        // still, in effect, unfilled, same exit code as if no fill existed for it at all.
        Assert.Equal(3, exitCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "generate", Package, "Fills")));

        var record = Assert.Single(ReadManifest());
        Assert.Equal(FillStatus.Stale, record.Status);
        Assert.Equal(GapId, record.GapId);
        Assert.Equal(OldHash, record.RecordedEvidenceSha256);
        Assert.Equal(CurrentHash, record.CurrentEvidenceSha256);
    }

    [Fact]
    public void Run_AppliesAFillAsUnattributed_WhenItCarriesNoProvenanceComment()
    {
        WriteGaps(ScriptComponentColumnGap(CurrentHash));
        WriteFill("FooTransform.Fills.cs", """
            namespace Pkg.Mapping;

            public sealed partial class FooTransform
            {
                private partial string Fill_Bar(FooRow row, in RowContext ctx) => "x";
            }
            """);

        var exitCode = ApplyFillsCommand.Run(["--out", _root]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(_root, "generate", Package, "Fills", "FooTransform.Fills.cs")));

        var record = Assert.Single(ReadManifest());
        Assert.Equal(FillStatus.Unattributed, record.Status);
        Assert.Null(record.Author);
        Assert.Null(record.RecordedEvidenceSha256);
        Assert.Equal(CurrentHash, record.CurrentEvidenceSha256);
    }

    [Fact]
    public void Run_ReportsAnOrphan_ForASeamNoGapAskedFor_AndDoesNotCopyIt()
    {
        // No gaps at all for this package -- a fill answering a seam that no longer exists.
        WriteGaps();
        WriteFill("FooTransform.Fills.cs", """
            namespace Pkg.Mapping;

            public sealed partial class FooTransform
            {
                private partial string Fill_Gone(FooRow row, in RowContext ctx) => "x";
            }
            """);

        var exitCode = ApplyFillsCommand.Run(["--out", _root]);

        Assert.Equal(1, exitCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "generate", Package, "Fills")));

        var record = Assert.Single(ReadManifest());
        Assert.Equal(FillStatus.Orphaned, record.Status);
        Assert.Null(record.GapId);
        Assert.Equal("Fill_Gone", record.Seam);
    }

    [Fact]
    public void Run_BlocksTheWholeFile_WhenOnlyOneOfItsSeamsIsStale()
    {
        WriteGaps(
            ScriptComponentColumnGap(CurrentHash),
            new GapSpec
            {
                GapId = "SCRIPT-COLUMN:Pkg:Foo.Baz", Package = Package, Kind = GapKind.ScriptComponentColumn,
                Tier = GapTier.MissingLogic, Location = "Foo.Baz", Reason = "produced by a Script Component",
                IsBlocking = true, EvidenceSha256 = CurrentHash,
            });
        WriteFill("FooTransform.Fills.cs", $$"""
            namespace Pkg.Mapping;

            public sealed partial class FooTransform
            {
                // ssisx-fill: GapId={{GapId}} Author=tester Date=2026-09-03 EvidenceSha256={{CurrentHash}}
                private partial string Fill_Bar(FooRow row, in RowContext ctx) => "x";

                // ssisx-fill: GapId=SCRIPT-COLUMN:Pkg:Foo.Baz Author=tester Date=2026-09-03 EvidenceSha256={{OldHash}}
                private partial string Fill_Baz(FooRow row, in RowContext ctx) => "y";
            }
            """);

        var exitCode = ApplyFillsCommand.Run(["--out", _root]);

        // Bar applies; Baz is stale -- so the seam is genuinely still outstanding.
        Assert.Equal(3, exitCode);

        // Fill_Bar's OWN evidence is current -- its manifest record still says so -- but Fill_Baz
        // sharing the same file is stale, and there is no safe way to copy one method without the
        // other, so NEITHER seam actually lands in the generated project.
        var records = ReadManifest().OrderBy(r => r.Seam, StringComparer.Ordinal).ToList();
        Assert.Equal(2, records.Count);
        Assert.Equal(FillStatus.Applied, Assert.Single(records, r => r.Seam == "Fill_Bar").Status);
        Assert.Equal(FillStatus.Stale, Assert.Single(records, r => r.Seam == "Fill_Baz").Status);

        Assert.False(Directory.Exists(Path.Combine(_root, "generate", Package, "Fills")));
    }

    [Fact]
    public void Run_RecognizesAScriptTaskFill_ByItsClassProvenanceComment()
    {
        var taskGapId = "SCRIPT-TASK:Pkg:SCR_Task.ScriptTask";
        var className = ScriptTaskEmitter.ClassName("SCR_Task");
        WriteGaps(new GapSpec
        {
            GapId = taskGapId, Package = Package, Kind = GapKind.ScriptTask, Tier = GapTier.MissingLogic,
            Location = "SCR_Task.ScriptTask", Reason = "a Microsoft.ScriptTask", IsBlocking = true,
            EvidenceSha256 = CurrentHash,
        });
        WriteFill($"{className}.Fills.cs", $$"""
            using Etl.Core.Abstractions;

            namespace Pkg.ScriptTasks;

            // ssisx-fill: GapId={{taskGapId}} Author=tester Date=2026-09-03 EvidenceSha256={{CurrentHash}}
            public sealed partial class {{className}}
            {
                private partial async Task RunScriptAsync(ScriptTaskContext ctx, CancellationToken ct)
                {
                    await Task.CompletedTask;
                }
            }
            """);

        var exitCode = ApplyFillsCommand.Run(["--out", _root]);

        Assert.Equal(0, exitCode);
        var record = Assert.Single(ReadManifest());
        Assert.Equal(FillStatus.Applied, record.Status);
        Assert.Equal($"{className}.RunScriptAsync", record.Seam);
        Assert.Equal(taskGapId, record.GapId);
    }

    [Fact]
    public void Run_NamesTheConformanceRuleId_WhenEveryColumnOfAScriptComponentIsFilled()
    {
        const string refId = "Package\\DFT_Load\\SCR_Cleanse";
        const string ruleId = "SCRIPT-CODE:Package\\DFT_Load\\SCR_Cleanse";
        WriteGaps(
            new GapSpec
            {
                GapId = "SCRIPT-COLUMN:Pkg:Foo.Bar", Package = Package, Kind = GapKind.ScriptComponentColumn,
                Tier = GapTier.MissingLogic, Location = "Foo.Bar", Reason = "produced by a Script Component",
                IsBlocking = true, EvidenceSha256 = CurrentHash, EvidenceRefId = refId,
            },
            new GapSpec
            {
                GapId = "SCRIPT-COLUMN:Pkg:Foo.Baz", Package = Package, Kind = GapKind.ScriptComponentColumn,
                Tier = GapTier.MissingLogic, Location = "Foo.Baz", Reason = "produced by a Script Component",
                IsBlocking = true, EvidenceSha256 = CurrentHash, EvidenceRefId = refId,
            });
        WriteClaim(ruleId, "Pending");
        WriteFill("FooTransform.Fills.cs", """
            namespace Pkg.Mapping;

            public sealed partial class FooTransform
            {
                private partial string Fill_Bar(FooRow row, in RowContext ctx) => "x";
                private partial string Fill_Baz(FooRow row, in RowContext ctx) => "y";
            }
            """);

        var output = CaptureConsole(() => ApplyFillsCommand.Run(["--out", _root]));

        Assert.Contains(ruleId, output);
        Assert.Contains("Pending", output);

        var records = ReadManifest();
        Assert.All(records, r => Assert.Equal(ruleId, r.ConformanceRuleId));
    }

    [Fact]
    public void Run_DoesNotNameTheRule_WhenOnlySomeOfItsColumnsAreFilled()
    {
        const string refId = "Package\\DFT_Load\\SCR_Cleanse";
        const string ruleId = "SCRIPT-CODE:Package\\DFT_Load\\SCR_Cleanse";
        WriteGaps(
            new GapSpec
            {
                GapId = "SCRIPT-COLUMN:Pkg:Foo.Bar", Package = Package, Kind = GapKind.ScriptComponentColumn,
                Tier = GapTier.MissingLogic, Location = "Foo.Bar", Reason = "produced by a Script Component",
                IsBlocking = true, EvidenceSha256 = CurrentHash, EvidenceRefId = refId,
            },
            new GapSpec
            {
                GapId = "SCRIPT-COLUMN:Pkg:Foo.Baz", Package = Package, Kind = GapKind.ScriptComponentColumn,
                Tier = GapTier.MissingLogic, Location = "Foo.Baz", Reason = "produced by a Script Component",
                IsBlocking = true, EvidenceSha256 = CurrentHash, EvidenceRefId = refId,
            });
        // Only Fill_Bar exists -- Fill_Baz has no fill at all, so the component's own obligation
        // (port the WHOLE thing) is genuinely not finished yet.
        WriteFill("FooTransform.Fills.cs", """
            namespace Pkg.Mapping;

            public sealed partial class FooTransform
            {
                private partial string Fill_Bar(FooRow row, in RowContext ctx) => "x";
            }
            """);

        var output = CaptureConsole(() => ApplyFillsCommand.Run(["--out", _root]));

        Assert.DoesNotContain(ruleId, output);

        // The single filled seam still carries its own RuleId in the manifest -- that fact is true
        // regardless of whether the group as a whole is done.
        var record = ReadManifest().Single(r => r.Seam == "Fill_Bar");
        Assert.Equal(ruleId, record.ConformanceRuleId);
    }

    [Fact]
    public void Run_ReportsNoClaimsFileFound_WhenConformanceWasNeverRun()
    {
        const string refId = "Package\\SCR_Task";
        const string ruleId = "SCRIPT-CODE:Package\\SCR_Task";
        WriteGaps(new GapSpec
        {
            GapId = "SCRIPT-TASK:Pkg:SCR_Task.ScriptTask", Package = Package, Kind = GapKind.ScriptTask,
            Tier = GapTier.MissingLogic, Location = "SCR_Task.ScriptTask", Reason = "a Microsoft.ScriptTask",
            IsBlocking = true, EvidenceSha256 = CurrentHash, EvidenceRefId = refId,
        });
        var className = ScriptTaskEmitter.ClassName("SCR_Task");
        WriteFill($"{className}.Fills.cs", $$"""
            using Etl.Core.Abstractions;

            namespace Pkg.ScriptTasks;

            public sealed partial class {{className}}
            {
                private partial Task RunScriptAsync(ScriptTaskContext ctx, CancellationToken ct) => Task.CompletedTask;
            }
            """);
        // Deliberately no conformance/claims directory at all -- nobody has run `ssisx conformance`.

        var output = CaptureConsole(() => ApplyFillsCommand.Run(["--out", _root]));

        Assert.Contains(ruleId, output);
        Assert.Contains("no claims file found", output);
    }
}
