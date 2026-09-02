using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Covers a Lookup's <c>NoMatchBehavior</c> and <c>TreatDuplicateKeysAsError</c>, neither of
/// which codegen read before the 2026-09-02 gap audit -- <c>NoMatchBehaviorRaw</c> was extracted
/// but had no reader anywhere in the project, and <c>TreatDuplicateKeysAsError</c> was not even
/// extracted.
///
/// <para>What the generated code does on a MISS is decided entirely by whether the flow filters:
/// with filtering the row is dropped before the transform; without it the transform indexes the
/// cache and therefore THROWS. Neither is right for every setting, so the setting has to be
/// checked. Measured/evidenced meanings: <b>0</b> fails the component on a miss (measured via
/// dtexec -- one unmatched row makes the package return DTSER_FAILURE and load nothing, so a
/// throwing indexer is faithful); <b>1</b> redirects misses to the No Match output (evidenced on
/// every real routed-no-match Lookup in the corpus, including the third-party
/// Package_Transforms); <b>2</b> ignores the failure and passes NULLs, which is unevidenced and
/// would need every Lookup-added column made nullable.</para>
///
/// <para>The two variant fixtures are byte-preserving derivations of SyntheticLookupSingle.dtsx
/// changing only the one property each. Flipping these raw values does NOT produce a package real
/// SSIS will run (it couples NoMatchBehavior to the output's row disposition, so a bare flip
/// validates as VS_ISCORRUPT) -- but ssisx parses the saved XML directly and never invokes the
/// SSIS object model, so they are perfectly valid inputs for proving the refusal logic, which is
/// all they are used for. Same reasoning as the Excel SqlCommand-mode fixtures.</para>
/// </summary>
public class LookupNoMatchTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Read_PromotesTreatDuplicateKeysAsError()
    {
        var package = LoadSyntheticFixture("SyntheticLookupSingle.dtsx");
        var pipeline = package.Executables.Single(e => e.DataFlowTask is not null).DataFlowTask!.Pipeline;
        var lookup = pipeline.Components.Single(c => c.ComponentClassId == "Microsoft.Lookup");

        Assert.Equal(0, lookup.Lookup!.NoMatchBehaviorRaw);
        Assert.False(lookup.Lookup!.TreatDuplicateKeysAsError);
    }

    [Fact]
    public void Emit_KeepsTheFirstDuplicateReferenceKey_MatchingMeasuredSsis()
    {
        var package = LoadSyntheticFixture("SyntheticLookupSingle.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var cache = result.Files.Single(f => f.RelativePath.EndsWith("Cache.cs", StringComparison.Ordinal)).Content;
        // Measured: a reference query returning Canada twice (CA then CA-DUP) resolves every
        // match to CA. The emitter used to write "result[keySelector(row)] = row" -- last wins --
        // which would have joined against CA-DUP instead, silently, on every duplicated key.
        Assert.Contains("result.TryAdd(keySelector(row), row);", cache);
        Assert.DoesNotContain("result[keySelector(row)] = row;", cache);
    }

    [Fact]
    public void Generate_AcceptsFailOnNoMatch_WhenNothingFiltersTheMissesAway()
    {
        // NoMatchBehavior=0 with no filtering: the transform's cache indexer throws, and SSIS
        // fails the component. Both fail, so the translation is faithful and must NOT gap.
        var package = LoadSyntheticFixture("SyntheticLookupSingle.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.DoesNotContain(result.Gaps, g => g.IsBlocking);
        Assert.Contains(result.Files, f => f.RelativePath.EndsWith("Program.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_AcceptsRedirect_WhenTheMissesAreProvablyExcluded()
    {
        // NoMatchBehavior=1 whose no-match output is a discarded dead-end RowCount: generated
        // code filters those rows out, which is what SSIS effectively does. Must not gap -- this
        // is the shape the real portfolio's own DFT_LookupAndAggregate has.
        var package = LoadSyntheticFixture("SyntheticLookupThenAggregate.dtsx");
        var pipeline = package.Executables.Single(e => e.DataFlowTask is not null).DataFlowTask!.Pipeline;
        Assert.Equal(1, pipeline.Components.Single(c => c.ComponentClassId == "Microsoft.Lookup").Lookup!.NoMatchBehaviorRaw);

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.DoesNotContain(result.Gaps, g => g.IsBlocking && g.Reason.Contains("NoMatchBehavior", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_ReportsAGap_ForIgnoreFailure_WhichWouldThrowWhereSsisPassesNulls()
    {
        var package = LoadSyntheticFixture("SyntheticLookupIgnoreFailure.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var gap = Assert.Single(result.Gaps, g => g.Reason.Contains("NoMatchBehavior=2", StringComparison.Ordinal));
        Assert.True(gap.IsBlocking);
        Assert.DoesNotContain(result.Files, f => f.RelativePath.EndsWith("Program.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_ReportsAGap_WhenDuplicateKeysMustBeAnError()
    {
        var package = LoadSyntheticFixture("SyntheticLookupDuplicateError.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        // The generated cache keeps the first occurrence and cannot fail on a duplicate, so
        // honouring this setting is impossible -- say so rather than ignoring the request.
        var gap = Assert.Single(result.Gaps, g => g.Reason.Contains("TreatDuplicateKeysAsError=true", StringComparison.Ordinal));
        Assert.True(gap.IsBlocking);
    }
}
