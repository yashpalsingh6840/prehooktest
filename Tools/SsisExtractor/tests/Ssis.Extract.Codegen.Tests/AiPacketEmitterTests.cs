using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

public class AiPacketEmitterTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Emit_IndexesEveryGap_ButWritesAPacketOnlyForTier1And2()
    {
        var package = LoadSyntheticFixture("SyntheticLookupSplit.dtsx");
        var gaps = new List<GenerationGap>
        {
            new("DFT_LookupSplitDemo", "lookup", true, GapKind.LookupJoinKey,
                TestFixtures.FindComponent(package, "Microsoft.Lookup", "Lookup").RefId),
            new("SomeFlow", "an unsupported component type", true),
            new("Package.Notification", "no recipients in a .dtsx", false),
        };

        var result = AiPacketEmitter.Emit(package, gaps);

        // Every gap is indexed, packet or not -- gaps.json is the complete picture.
        Assert.Equal(3, result.Gaps.Count);
        // ...but only the Tier-1 gap gets a work packet.
        var packet = Assert.Single(result.Packets);
        Assert.EndsWith(".md", packet.RelativePath);

        var tier3 = result.Gaps.Single(g => g.Location == "SomeFlow");
        Assert.Equal(GapTier.MissingToolSupport, tier3.Tier);
        Assert.Null(tier3.PacketPath);
        Assert.Null(tier3.EvidenceSha256);

        var advisory = result.Gaps.Single(g => g.Location == "Package.Notification");
        Assert.Equal(GapTier.Advisory, advisory.Tier);
        Assert.Null(advisory.PacketPath);
    }

    [Fact]
    public void Emit_MergesAScriptTaskSeamAndItsCompanionTestOracleGap_IntoOnePacketFile()
    {
        // Phase 5.4 (G5): PackageGenerator.Generate against a real seam-bearing fixture already
        // produces a ScriptTask gap and its own companion TestOracle gap (same Location, per
        // ScriptTaskEmitter.cs's own doc comment) -- confirm AiPacketEmitter folds them into ONE
        // file rather than two, with both GapIds still present and pointing at it.
        var package = LoadSyntheticFixture("SyntheticScriptTaskSeams.dtsx");
        var generated = PackageGenerator.Generate(package, namespacePrefix: null, emitSeams: true);
        var scriptTaskGaps = generated.Gaps.Where(g => g.Kind == GapKind.ScriptTask).ToList();
        Assert.NotEmpty(scriptTaskGaps); // sanity: this fixture genuinely has one, or the test is vacuous.
        var seamGap = scriptTaskGaps[0];
        var companionGap = Assert.Single(generated.Gaps, g => g.Kind == GapKind.TestOracle && g.Location == seamGap.Location);

        var result = AiPacketEmitter.Emit(package, generated.Gaps, generated.Files);

        var seamSpec = Assert.Single(result.Gaps, g => g.Kind == GapKind.ScriptTask && g.Location == seamGap.Location);
        var testSpec = Assert.Single(result.Gaps, g => g.Kind == GapKind.TestOracle && g.Location == companionGap.Location);
        Assert.NotNull(seamSpec.PacketPath);
        Assert.Equal(seamSpec.PacketPath, testSpec.PacketPath); // same file.
        // Each gap's own evidence hash is still independently computed -- the merge only changes
        // where the text is PRINTED, never what ApplyFillsCommand checks a fill's hash against.
        Assert.NotEqual(seamSpec.EvidenceSha256, testSpec.EvidenceSha256);

        var packet = Assert.Single(result.Packets, p => p.RelativePath == seamSpec.PacketPath);
        Assert.Contains($"`{seamSpec.GapId}` + `{testSpec.GapId}`", packet.Content);
        Assert.Contains("## Answer 1", packet.Content);
        Assert.Contains("## Answer 2", packet.Content);
        // The evidence section (which embeds the real script source) appears exactly once, not
        // duplicated across two answer sections.
        Assert.Equal(1, packet.Content.Split("## Evidence from the package").Length - 1);

        // Only ONE .md file was written for this seam+companion pair, not two -- the file count
        // is strictly less than the count of gaps that carry a packet.
        Assert.True(result.Packets.Count < result.Gaps.Count(g => g.PacketPath is not null));
    }

    [Fact]
    public void Emit_BuildsALookupPacket_FromTheRealReferenceQueryAndColumns()
    {
        var package = LoadSyntheticFixture("SyntheticLookupSplit.dtsx");
        var lookup = TestFixtures.FindComponent(package, "Microsoft.Lookup", "Lookup");
        var gaps = new List<GenerationGap> { new("DFT_LookupSplitDemo", "lookup", true, GapKind.LookupJoinKey, lookup.RefId) };

        var result = AiPacketEmitter.Emit(package, gaps);
        var content = Assert.Single(result.Packets).Content;

        // The evidence a reviewer needs to name the join key: the real reference SQL and the
        // real reference columns, resolved to C# types (the reference columns carry the DT_
        // prefixed type spelling, which has to be normalized before the type map will resolve it).
        Assert.Contains("SELECT CustomerID, CustomerName, Region FROM dbo.SyntheticCustomer", content);
        Assert.Contains("| `CustomerID` | `DT_I4` | - | `int` |", content);
        Assert.Contains("| `CustomerName` | `DT_WSTR` | 100 | `string` |", content);

        // Phase 4.4 (P2): the UPSTREAM columns actually available to join on -- the OLE DB
        // Source feeding this Lookup -- so a reviewer isn't left guessing whether a candidate
        // input column is a real, available value or something only computed further upstream.
        Assert.Contains("### Upstream columns available to join on (from `OLE DB Source`, feeding this Lookup)", content);
        Assert.Contains("| `CustomerID` | `i4` | - | `int` |", content);

        // A Tier-1 answer is a FACT, not code -- the packet must ask for JSON and must not drag
        // in the expression-semantics appendix, which is noise for this decision.
        Assert.Contains("\"inputColumn\"", content);
        Assert.DoesNotContain("SSIS semantics you must preserve", content);

        var spec = Assert.Single(result.Gaps);
        Assert.Equal(GapTier.MissingDatum, spec.Tier);
        Assert.NotNull(spec.EvidenceSha256);
    }

    [Fact]
    public void Emit_BuildsAScriptComponentPacket_WithTheColumnTables_ButOmitsTheSemanticsAppendix_WhenTheScriptIsAlreadyCSharp()
    {
        // Phase 5.5 (P4/G3): SyntheticScriptComponent.dtsx's own Script Component declares
        // Language="CSharp" -- a script already written in C# has no SSIS expression logic to
        // reproduce, so IsCSharpScript gates the appendix off entirely here (see AiPacketEmitter's
        // own doc comment). The VB counterpart (appendix stays) is asserted by
        // Emit_BuildsAScriptTaskPacket_KeepsTheSemanticsAppendix_WhenTheScriptIsVisualBasic below.
        var package = LoadSyntheticFixture("SyntheticScriptComponent.dtsx");
        var component = package.Executables
            .SelectMany(e => e.DataFlowTask?.Pipeline.Components ?? [])
            .Single(c => c.ScriptComponent is not null);
        Assert.Equal("CSharp", component.ScriptComponent!.Language); // sanity: this fixture is genuinely C#, or the test is vacuous.
        var gaps = new List<GenerationGap>
        {
            new("Target.SomeColumn", "produced by a Script Component", true, GapKind.ScriptComponentColumn, component.RefId),
        };

        var result = AiPacketEmitter.Emit(package, gaps);
        var content = Assert.Single(result.Packets).Content;

        Assert.Contains("### Input columns available on `row`", content);
        Assert.Contains("### Columns this component produces", content);
        Assert.DoesNotContain("SSIS semantics you must preserve", content);
        Assert.DoesNotContain("rounds half-to-EVEN", content);
        Assert.Contains("fenced `csharp` block", content);
    }

    [Fact]
    public void Emit_BuildsAScriptTaskPacket_KeepsTheSemanticsAppendix_WhenTheScriptIsVisualBasic()
    {
        // The mirror-image case: a VB script still has SSIS-expression-shaped logic worth
        // measuring against (e.g. the DT_I4 half-to-even rounding rule), so the appendix must
        // stay for it.
        var package = LoadSyntheticFixture("SyntheticScriptTaskSeams.dtsx");
        var generated = PackageGenerator.Generate(package, namespacePrefix: null, emitSeams: true);
        var scriptTaskGaps = generated.Gaps.Where(g => g.Kind == GapKind.ScriptTask).ToList();
        Assert.NotEmpty(scriptTaskGaps); // sanity: this fixture genuinely has at least one, or the test is vacuous.
        var seamGap = scriptTaskGaps[0];
        var task = PackageTree.AllExecutables(package).Single(e => e.RefId == seamGap.EvidenceRefId);
        Assert.Equal("VisualBasic", task.ScriptTask!.Language); // sanity: this fixture is genuinely VB, or the test is vacuous.

        var result = AiPacketEmitter.Emit(package, generated.Gaps, generated.Files);
        var gapSpec = result.Gaps.Single(g => g.Kind == GapKind.ScriptTask && g.Location == seamGap.Location);
        var packet = Assert.Single(result.Packets, p => p.RelativePath == gapSpec.PacketPath);

        Assert.Contains("SSIS semantics you must preserve", packet.Content);
        Assert.Contains("rounds half-to-EVEN", packet.Content);
    }

    /// <summary>
    /// The evidence hash is what makes a fill checkable against the .dtsx it was written for
    /// (chunk 4's staleness check). It must be a pure function of the evidence -- stable across
    /// runs, and different when the evidence genuinely differs.
    /// </summary>
    [Fact]
    public void Emit_ProducesAStableEvidenceHash()
    {
        var package = LoadSyntheticFixture("SyntheticLookupSplit.dtsx");
        var lookup = TestFixtures.FindComponent(package, "Microsoft.Lookup", "Lookup");
        var gaps = new List<GenerationGap> { new("DFT_LookupSplitDemo", "lookup", true, GapKind.LookupJoinKey, lookup.RefId) };

        var first = AiPacketEmitter.Emit(package, gaps).Gaps.Single().EvidenceSha256;
        var second = AiPacketEmitter.Emit(package, gaps).Gaps.Single().EvidenceSha256;

        Assert.Equal(first, second);
        Assert.Equal(64, first!.Length);
    }

    /// <summary>
    /// A refId that resolves to nothing must say so IN the packet rather than throw or emit a
    /// confident-looking packet with no evidence -- the same "a gap must be visible" rule the
    /// rest of this tool follows.
    /// </summary>
    [Fact]
    public void Emit_SaysSoExplicitly_WhenTheEvidenceRefIdCannotBeResolved()
    {
        var package = LoadSyntheticFixture("SyntheticLookupSplit.dtsx");
        var gaps = new List<GenerationGap> { new("X", "lookup", true, GapKind.LookupJoinKey, "no-such-refid") };

        var content = Assert.Single(AiPacketEmitter.Emit(package, gaps).Packets).Content;

        Assert.Contains("Could not resolve", content);
        Assert.Contains("it is a bug in AiPacketEmitter", content);
    }

    // Docs/Generated-Tests-Plan.md phase 3: GapKind.TestOracle/LocalFileSourceData, both
    // MissingDatum tier (non-blocking, still get a packet -- see GapIdentityTests for that half).

    [Fact]
    public void Emit_BuildsATestOracleRouterPacket_FromTheRealConditionalSplitCases()
    {
        var package = LoadSyntheticFixture("SyntheticConditionalSplit.dtsx");
        var split = TestFixtures.FindComponent(package, "Microsoft.ConditionalSplit", "Conditional Split");
        var gaps = new List<GenerationGap>
        {
            new("SyntheticConditionalSplitRouter", "case could not be evaluated", false, GapKind.TestOracle, split.RefId),
        };

        var result = AiPacketEmitter.Emit(package, gaps);
        var spec = Assert.Single(result.Gaps);
        Assert.Equal(GapTier.MissingDatum, spec.Tier);
        Assert.False(spec.IsBlocking);

        var content = Assert.Single(result.Packets).Content;
        Assert.Contains("### Every case, in evaluation order", content);
        Assert.Contains("### Inputs available on `row`", content);
        // Phase 4.3 (G4/a): TEST-ORACLE gets its OWN tier banner -- unlike LookupJoinKey/
        // EncryptedConnectionManagerSecret, the answer here genuinely IS a whole test file, so it
        // must not say "Do not write code for this."
        Assert.Contains("Supply a whole test FILE; nothing here generates it for you.", content);
        Assert.DoesNotContain("Do not write code for this.", content);
        Assert.Contains("self-contained xUnit test FILE", content);
        Assert.Contains("<Package>.Tests/Fills/", content);
    }

    /// <summary>
    /// Phase 4.3 (G4/d) of the independent-review fix plan: a real, previously-shipped bug --
    /// the router packet's own input table listed a Conditional Split condition's raw PIPELINE
    /// BUFFER name/type (e.g. `CustomerId_i4 : i4`), which is not what the GENERATED router
    /// actually references at all when that column is produced by an upstream Data Conversion.
    /// The real one, `SyntheticDataConversionSplit.dtsx`'s own `CSPLIT_Validity`
    /// (`!ISNULL(CustomerId_i4)`), resolves through `SsisFn.ToNullableI4(row.CustomerIdText)` on
    /// a `string`-typed row property named `CustomerIdText` -- nothing a reader could have
    /// guessed from the old buffer-name table.
    /// </summary>
    [Fact]
    public void Emit_ListsARouterInputProducedByDataConversion_AsTheGeneratedRowTypeActuallyResolvesIt()
    {
        var package = LoadSyntheticFixture("SyntheticDataConversionSplit.dtsx");
        var split = TestFixtures.FindComponent(package, "Microsoft.ConditionalSplit", "CSPLIT_Validity");
        var gaps = new List<GenerationGap>
        {
            new("CSPLIT_ValidityRouter", "case could not be evaluated", false, GapKind.TestOracle, split.RefId),
        };

        var content = Assert.Single(AiPacketEmitter.Emit(package, gaps).Packets).Content;

        Assert.Contains("| `CustomerId_i4` | `SsisFn.ToNullableI4(row.CustomerIdText)` | `I4?` |", content);
        // The plain passthrough columns must still resolve the ordinary way, unaffected.
        Assert.Contains("| `ID` | `row.ID` | `I4` |", content);
        Assert.DoesNotContain("i4 |", content); // the OLD buffer-type spelling this replaces
    }

    /// <summary>
    /// Added 2026-09-06 after a real gap (Package_Transforms's own RegionSummary, whose GroupBy
    /// key resolves through a Lookup cache) fell through to the generic "could not resolve" text
    /// -- TestOracleEvidence only ever checked ScriptComponent/ConditionalSplit, never Aggregate,
    /// even though FindComponent already resolved the component correctly. Guards against that
    /// 4th shape regressing back to the fallback message.
    /// </summary>
    [Fact]
    public void Emit_BuildsATestOracleAggregatePacket_FromTheRealGroupByAndCountColumns()
    {
        var package = LoadSyntheticFixture("SyntheticAggregate.dtsx");
        var aggregate = TestFixtures.FindComponent(package, "Microsoft.Aggregate", "AGG_ByRegion");
        var gaps = new List<GenerationGap>
        {
            new("RegionSummary", "GroupBy value only resolves through a Lookup cache", false, GapKind.TestOracle, aggregate.RefId),
        };

        var result = AiPacketEmitter.Emit(package, gaps);
        var spec = Assert.Single(result.Gaps);
        Assert.Equal(GapTier.MissingDatum, spec.Tier);
        Assert.False(spec.IsBlocking);

        var content = Assert.Single(result.Packets).Content;
        // Must NOT fall through to the generic unresolved-evidence message.
        Assert.DoesNotContain("Could not resolve the source of this test-oracle gap", content);
        Assert.Contains("### Every aggregate column, in declared order", content);
        Assert.Contains("| `Region` | GroupBy |", content);
        Assert.Contains("| `CustomerCount` | Count |", content);
        Assert.Contains("AGG_ByRegion", content);
    }

    [Fact]
    public void Emit_BuildsALocalFileSourceDataPacket_FromTheRealFlatFileSchema()
    {
        var package = LoadSyntheticFixture("SyntheticPostFlowSql.dtsx");
        var cm = package.ConnectionManagers.Single(c => c.ObjectName == "CM_PostFlowCsv");
        var gaps = new List<GenerationGap>
        {
            new("SyntheticPostFlowTarget", "still reads a deterministic synthetic sample", false, GapKind.LocalFileSourceData, cm.RefId),
        };

        var result = AiPacketEmitter.Emit(package, gaps);
        var spec = Assert.Single(result.Gaps);
        Assert.Equal(GapTier.MissingDatum, spec.Tier);
        Assert.NotNull(spec.ExpectedFileName);

        var content = Assert.Single(result.Packets).Content;
        Assert.Contains($"**Save as:** `TestData/{spec.ExpectedFileName}`", content);
        Assert.Contains("### Columns, in file order", content);
        // No provenance mechanism for a raw data file -- stated plainly, not silently absent.
        Assert.Contains("no provenance comment convention for a data file", content);
        Assert.DoesNotContain("ssisx-fill:", content);
    }

    [Fact]
    public void Emit_ResolvesExpectedFileName_FromTheConnectionManagersOwnConfiguredPath_NotTheTierASampleName()
    {
        // GapSpec.ExpectedFileName's own doc comment: appsettings.Development.json overrides only
        // SourceFolder, never SourceFileName, so a real TestData/ fill must be named exactly what
        // the connection manager's own design-time path already says -- which is frequently NOT
        // the Tier-A synthetic sample's own "{FileSourceKey}.csv" name.
        var package = LoadSyntheticFixture("SyntheticPostFlowSql.dtsx");
        var cm = package.ConnectionManagers.Single(c => c.ObjectName == "CM_PostFlowCsv");
        var gaps = new List<GenerationGap>
        {
            new("SyntheticPostFlowTarget", "reason", false, GapKind.LocalFileSourceData, cm.RefId),
        };

        var spec = Assert.Single(AiPacketEmitter.Emit(package, gaps).Gaps);

        Assert.Equal(Path.GetFileName(cm.Parsed!.FilePath), spec.ExpectedFileName);
    }
}
