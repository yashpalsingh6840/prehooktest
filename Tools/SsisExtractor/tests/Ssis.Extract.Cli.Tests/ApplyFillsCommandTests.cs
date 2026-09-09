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
    // "SCR_Bar" is the seam name -- since the combined-seam round, a GapId's own Location is
    // "{Entity}.{SanitizedComponentName}" and TransformEmitter names the seam method IDENTICALLY
    // to that tail (no more synthesized "Fill_" + column). One gap per COMPONENT now, not per
    // column -- see TransformEmitter.ScriptComponentGroup's own doc comment.
    private const string GapId = "SCRIPT-COLUMN:Pkg:Foo.SCR_Bar";
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

    private string WriteSubFill(string subfolder, string fileName, string content)
    {
        var dir = Path.Combine(_root, "fills", Package, subfolder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private List<FillRecordSpec> ReadManifest() =>
        JsonSerializer.Deserialize<List<FillRecordSpec>>(File.ReadAllText(Path.Combine(_root, "fills-applied.json")))!;

    private static GapSpec ScriptComponentColumnGap(string evidenceHash, string? evidenceRefId = null) => new()
    {
        GapId = GapId, Package = Package, Kind = GapKind.ScriptComponentColumn, Tier = GapTier.MissingLogic,
        Location = "Foo.SCR_Bar", Reason = "produced by a Script Component", IsBlocking = true,
        EvidenceSha256 = evidenceHash, EvidenceRefId = evidenceRefId,
    };

    private const string TestOracleGapId = "TEST-ORACLE:Pkg:CSPLIT_X";
    private static GapSpec TestOracleGap(string evidenceHash) => new()
    {
        GapId = TestOracleGapId, Package = Package, Kind = GapKind.TestOracle, Tier = GapTier.MissingDatum,
        Location = "CSPLIT_X", Reason = "no generated test at all", IsBlocking = false,
        EvidenceSha256 = evidenceHash,
    };

    private const string LocalDataGapId = "LOCAL-DATA:Pkg:FF_SRC_Foo";
    private static GapSpec LocalDataGap() => new()
    {
        GapId = LocalDataGapId, Package = Package, Kind = GapKind.LocalFileSourceData, Tier = GapTier.MissingDatum,
        Location = "FF_SRC_Foo", Reason = "still reads a deterministic synthetic sample", IsBlocking = false,
        ExpectedFileName = "Foo.csv",
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
                private partial SCR_BarResult SCR_Bar(FooRow row, in RowContext ctx) => new("x");
            }
            """);

        var exitCode = ApplyFillsCommand.Run(["--out", _root]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(_root, "generate", Package, "Fills", "FooTransform.Fills.cs")));

        var record = Assert.Single(ReadManifest());
        Assert.Equal(FillStatus.Applied, record.Status);
        Assert.Equal(GapId, record.GapId);
        Assert.Equal("SCR_Bar", record.Seam);
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
                private partial SCR_BarResult SCR_Bar(FooRow row, in RowContext ctx) => new("x");
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
                private partial SCR_BarResult SCR_Bar(FooRow row, in RowContext ctx) => new("x");
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
        // Two DISTINCT Script Components' seams landing in the same fill file -- a component's own
        // seam is now singular (one method, not one per column), so "two seams, one stale" needs
        // two separate components rather than two columns of the same one.
        WriteGaps(
            ScriptComponentColumnGap(CurrentHash),
            new GapSpec
            {
                GapId = "SCRIPT-COLUMN:Pkg:Foo.SCR_Baz", Package = Package, Kind = GapKind.ScriptComponentColumn,
                Tier = GapTier.MissingLogic, Location = "Foo.SCR_Baz", Reason = "produced by a Script Component",
                IsBlocking = true, EvidenceSha256 = CurrentHash,
            });
        WriteFill("FooTransform.Fills.cs", $$"""
            namespace Pkg.Mapping;

            public sealed partial class FooTransform
            {
                // ssisx-fill: GapId={{GapId}} Author=tester Date=2026-09-03 EvidenceSha256={{CurrentHash}}
                private partial SCR_BarResult SCR_Bar(FooRow row, in RowContext ctx) => new("x");

                // ssisx-fill: GapId=SCRIPT-COLUMN:Pkg:Foo.SCR_Baz Author=tester Date=2026-09-03 EvidenceSha256={{OldHash}}
                private partial SCR_BazResult SCR_Baz(FooRow row, in RowContext ctx) => new("y");
            }
            """);

        var exitCode = ApplyFillsCommand.Run(["--out", _root]);

        // SCR_Bar applies; SCR_Baz is stale -- so the seam is genuinely still outstanding.
        Assert.Equal(3, exitCode);

        // SCR_Bar's OWN evidence is current -- its manifest record still says so -- but SCR_Baz
        // sharing the same file is stale, and there is no safe way to copy one method without the
        // other, so NEITHER seam actually lands in the generated project.
        var records = ReadManifest().OrderBy(r => r.Seam, StringComparer.Ordinal).ToList();
        Assert.Equal(2, records.Count);
        Assert.Equal(FillStatus.Applied, Assert.Single(records, r => r.Seam == "SCR_Bar").Status);
        Assert.Equal(FillStatus.Stale, Assert.Single(records, r => r.Seam == "SCR_Baz").Status);

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

    // These two tests used to model "one Script Component, two columns, one filled one not" --
    // that shape became structurally impossible once a component has exactly ONE combined seam
    // (see TransformEmitter.ScriptComponentGroup). Retired and replaced with the scenario that's
    // still real: two DISTINCT components, each with its own single seam, one filled and one not --
    // proving partial PACKAGE-level completion still works correctly even though partial
    // COMPONENT-level completion no longer exists as a concept.
    [Fact]
    public void Run_NamesTheConformanceRuleId_OnlyForTheComponentWhoseSeamIsActuallyFilled()
    {
        const string refIdBar = "Package\\DFT_Load\\SCR_Bar";
        const string ruleIdBar = "SCRIPT-CODE:Package\\DFT_Load\\SCR_Bar";
        const string refIdBaz = "Package\\DFT_Load\\SCR_Baz";
        const string ruleIdBaz = "SCRIPT-CODE:Package\\DFT_Load\\SCR_Baz";
        WriteGaps(
            ScriptComponentColumnGap(CurrentHash, refIdBar),
            new GapSpec
            {
                GapId = "SCRIPT-COLUMN:Pkg:Foo.SCR_Baz", Package = Package, Kind = GapKind.ScriptComponentColumn,
                Tier = GapTier.MissingLogic, Location = "Foo.SCR_Baz", Reason = "produced by a Script Component",
                IsBlocking = true, EvidenceSha256 = CurrentHash, EvidenceRefId = refIdBaz,
            });
        WriteClaim(ruleIdBar, "Pending");
        // Only SCR_Bar has a fill -- SCR_Baz's own component is genuinely not ported yet.
        WriteFill("FooTransform.Fills.cs", """
            namespace Pkg.Mapping;

            public sealed partial class FooTransform
            {
                private partial SCR_BarResult SCR_Bar(FooRow row, in RowContext ctx) => new("x");
            }
            """);

        var output = CaptureConsole(() => ApplyFillsCommand.Run(["--out", _root]));

        Assert.Contains(ruleIdBar, output);
        Assert.Contains("Pending", output);
        Assert.DoesNotContain(ruleIdBaz, output);

        var record = ReadManifest().Single(r => r.Seam == "SCR_Bar");
        Assert.Equal(ruleIdBar, record.ConformanceRuleId);
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

    // Docs/Generated-Tests-Plan.md phase 3: fills/<Package>/Tests/*.cs (TEST-ORACLE) and
    // fills/<Package>/TestData/* (LOCAL-DATA) -- two new, structurally different routing shapes
    // from the seam mechanism above (a whole new file, not a partial-method splice).

    [Fact]
    public void Run_AppliesATestOracleFill_WhenItsLeadingProvenanceHashMatchesTheCurrentGap()
    {
        WriteGaps(TestOracleGap(CurrentHash));
        WriteSubFill("Tests", "CSPLIT_XTests.cs", $$"""
            // ssisx-fill: GapId={{TestOracleGapId}} Author=tester Date=2026-09-06 EvidenceSha256={{CurrentHash}}
            using Xunit;

            namespace Pkg.Tests;

            public class CSPLIT_XTests
            {
                [Fact]
                public void Placeholder() => Assert.True(true);
            }
            """);

        var exitCode = ApplyFillsCommand.Run(["--out", _root]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(_root, "generate", $"{Package}.Tests", "Fills", "CSPLIT_XTests.cs")));

        var record = Assert.Single(ReadManifest());
        Assert.Equal(FillStatus.Applied, record.Status);
        Assert.Equal(TestOracleGapId, record.GapId);
        Assert.Equal("tester", record.Author);
        Assert.Equal(CurrentHash, record.RecordedEvidenceSha256);
    }

    [Fact]
    public void Run_RefusesAStaleTestOracleFill_AndDoesNotCopyIt()
    {
        WriteGaps(TestOracleGap(CurrentHash));
        WriteSubFill("Tests", "CSPLIT_XTests.cs", $$"""
            // ssisx-fill: GapId={{TestOracleGapId}} Author=tester Date=2026-08-01 EvidenceSha256={{OldHash}}
            using Xunit;

            namespace Pkg.Tests;

            public class CSPLIT_XTests
            {
                [Fact]
                public void Placeholder() => Assert.True(true);
            }
            """);

        var exitCode = ApplyFillsCommand.Run(["--out", _root]);

        // A stale TEST-ORACLE fill still bumps the exit code (same 3 as a stale seam) -- it HAS a
        // fill, just an out-of-date one, worth flagging just as loudly even though the gap itself
        // is non-blocking for the BUILD.
        Assert.Equal(3, exitCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "generate", $"{Package}.Tests", "Fills")));

        var record = Assert.Single(ReadManifest());
        Assert.Equal(FillStatus.Stale, record.Status);
        Assert.Equal(OldHash, record.RecordedEvidenceSha256);
        Assert.Equal(CurrentHash, record.CurrentEvidenceSha256);
    }

    [Fact]
    public void Run_ReportsATestOracleFileAsOrphaned_WhenItCarriesNoProvenanceCommentAtAll()
    {
        // Unlike a ScriptComponentColumn/ScriptTask fill, there is no seam NAME to fall back on
        // here -- a whole new test file with no comment cannot be attributed to any gap at all.
        WriteGaps(TestOracleGap(CurrentHash));
        WriteSubFill("Tests", "CSPLIT_XTests.cs", """
            using Xunit;

            namespace Pkg.Tests;

            public class CSPLIT_XTests
            {
                [Fact]
                public void Placeholder() => Assert.True(true);
            }
            """);

        var exitCode = ApplyFillsCommand.Run(["--out", _root]);

        Assert.Equal(1, exitCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "generate", $"{Package}.Tests", "Fills")));

        var record = Assert.Single(ReadManifest());
        Assert.Equal(FillStatus.Orphaned, record.Status);
        Assert.Null(record.GapId);
    }

    [Fact]
    public void Run_ReportsATestOracleFileAsOrphaned_WhenItsGapIdDoesNotMatchAnyCurrentGap()
    {
        WriteGaps(TestOracleGap(CurrentHash));
        WriteSubFill("Tests", "CSPLIT_XTests.cs", $$"""
            // ssisx-fill: GapId=TEST-ORACLE:Pkg:SomethingThatNoLongerExists Author=tester Date=2026-09-06 EvidenceSha256={{CurrentHash}}
            using Xunit;

            namespace Pkg.Tests;

            public class CSPLIT_XTests
            {
                [Fact]
                public void Placeholder() => Assert.True(true);
            }
            """);

        var exitCode = ApplyFillsCommand.Run(["--out", _root]);

        Assert.Equal(1, exitCode);
        var record = Assert.Single(ReadManifest());
        Assert.Equal(FillStatus.Orphaned, record.Status);
    }

    [Fact]
    public void Run_AppliesALocalDataFill_WhenItsFileNameMatchesTheExpectedName()
    {
        WriteGaps(LocalDataGap());
        WriteSubFill("TestData", "Foo.csv", "ID,Name\r\n1,Alice\r\n");

        var exitCode = ApplyFillsCommand.Run(["--out", _root]);

        Assert.Equal(0, exitCode);
        var copied = Path.Combine(_root, "generate", Package, "TestData", "Foo.csv");
        Assert.True(File.Exists(copied));
        Assert.Equal("ID,Name\r\n1,Alice\r\n", File.ReadAllText(copied));

        var record = Assert.Single(ReadManifest());
        Assert.Equal(FillStatus.Applied, record.Status);
        Assert.Equal(LocalDataGapId, record.GapId);
        // No staleness concept for a raw data file -- see LocalFileSourceDataContract's own doc.
        Assert.Null(record.RecordedEvidenceSha256);
    }

    [Fact]
    public void Run_ReportsALocalDataFileAsOrphaned_WhenItsFileNameDoesNotMatchAnyCurrentGap()
    {
        WriteGaps(LocalDataGap());
        WriteSubFill("TestData", "WrongName.csv", "ID,Name\r\n1,Alice\r\n");

        var exitCode = ApplyFillsCommand.Run(["--out", _root]);

        Assert.Equal(1, exitCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "generate", Package, "TestData")));

        var record = Assert.Single(ReadManifest());
        Assert.Equal(FillStatus.Orphaned, record.Status);
        Assert.Null(record.GapId);
    }

    [Fact]
    public void Run_ReportsALocalDataGap_WithNoExpectedFileName_AsNeverMatchable()
    {
        // A gap with no derivable file name (an expression-driven connection manager) has
        // nothing to match a fill's own name against -- confirmed by writing a file that would
        // match the gap's own Location instead, which must NOT be treated as a match.
        WriteGaps(new GapSpec
        {
            GapId = "LOCAL-DATA:Pkg:FF_SRC_NoPath", Package = Package, Kind = GapKind.LocalFileSourceData,
            Tier = GapTier.MissingDatum, Location = "FF_SRC_NoPath", Reason = "no design-time default path",
            IsBlocking = false, ExpectedFileName = null,
        });
        WriteSubFill("TestData", "FF_SRC_NoPath.csv", "ID,Name\r\n1,Alice\r\n");

        var exitCode = ApplyFillsCommand.Run(["--out", _root]);

        Assert.Equal(1, exitCode);
        var record = Assert.Single(ReadManifest());
        Assert.Equal(FillStatus.Orphaned, record.Status);
    }
}
