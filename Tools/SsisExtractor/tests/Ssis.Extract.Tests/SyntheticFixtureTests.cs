using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;

namespace Ssis.Extract.Tests;

/// <summary>
/// Assertion-based tests (not golden-file: these fixtures are expected to evolve, unlike
/// the frozen PoC packages) against two synthetic packages built via the real SSIS 17
/// object model specifically to exercise component/task types this PoC's own two packages
/// never used -- Lookup, Conditional Split, OLE DB Source, a ForEach Loop Container, and a
/// Script Task. See Tools/SsisExtractor/docs/report-schema.md "Synthetic component-coverage
/// fixtures" for how each fixture was built and what it's verified evidence of versus
/// best-effort. Fixtures live under Fixtures/ in this test project, not the PoC's own SSIS/
/// folder -- they aren't part of the PoC's deliverable.
/// </summary>
public class SyntheticFixtureTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;
    private static readonly string FixturesDir = Path.Combine(Path.GetDirectoryName(ThisFilePath())!, "Fixtures");

    private static string ForEachScriptPath => Path.Combine(FixturesDir, "SyntheticForEachScript.dtsx");
    private static string LookupSplitPath => Path.Combine(FixturesDir, "SyntheticLookupSplit.dtsx");

    [Fact]
    public void ForEachScript_ExtractsAt100PercentCoverage()
    {
        var package = DtsxPackageReader.Read(ForEachScriptPath, noRedact: false);

        // Ground truth this asserts against: before ForEachLoopPayload existed, the
        // <ForEachEnumerator>/<ForEachVariableMappings> elements were invisible to the
        // coverage accounting entirely (never counted unmapped, never modeled) -- this
        // number being 100 here is only honestly earned now that they're really parsed.
        Assert.Equal(100.0, package.Coverage.CoveragePercent);
    }

    [Fact]
    public void ForEachLoop_FileEnumeratorAndVariableMapping_ParseCorrectly()
    {
        var package = DtsxPackageReader.Read(ForEachScriptPath, noRedact: false);
        var loop = Assert.Single(package.Executables);

        Assert.Equal("STOCK:FOREACHLOOP", loop.ExecutableType);
        Assert.NotNull(loop.ForEachLoop);
        Assert.Equal("Microsoft.ForEachFileEnumerator", loop.ForEachLoop!.EnumeratorCreationName);
        Assert.Null(loop.ForEachLoop.RawEnumeratorObjectDataXml); // fully modeled, not a raw fallback

        Assert.NotNull(loop.ForEachLoop.FileEnumerator);
        Assert.Equal(@"C:\Temp\SsisPoC", loop.ForEachLoop.FileEnumerator!.Folder);
        Assert.Equal("*.csv", loop.ForEachLoop.FileEnumerator.FileSpec);
        Assert.Equal(0, loop.ForEachLoop.FileEnumerator.FileNameRetrievalTypeRaw);
        Assert.False(loop.ForEachLoop.FileEnumerator.Recurse);

        var mapping = Assert.Single(loop.ForEachLoop.VariableMappings);
        Assert.Equal("User::CurrentFile", mapping.VariableName);
        Assert.Equal(0, mapping.ValueIndex);
    }

    [Fact]
    public void ScriptTask_LanguageAndVariables_ParseCorrectly()
    {
        var package = DtsxPackageReader.Read(ForEachScriptPath, noRedact: false);
        var loop = Assert.Single(package.Executables);
        var scriptTask = Assert.Single(loop.Children);

        Assert.Equal("Microsoft.ScriptTask", scriptTask.ExecutableType);
        Assert.NotNull(scriptTask.ScriptTask);
        Assert.Equal("VisualBasic", scriptTask.ScriptTask!.Language);
        Assert.Equal(["User::CurrentFile"], scriptTask.ScriptTask.ReadOnlyVariables);
        Assert.Empty(scriptTask.ScriptTask.ReadWriteVariables);
    }

    [Fact]
    public void ScriptTask_SourceExtractedFromProjectItem_NotFromCompiledBinary()
    {
        // Ground truth this fixture's <ScriptProject> was extended with (see the .dtsx
        // itself): a real SSDT-authored Script Task stores its actual source as plain CDATA
        // text in <ProjectItem>, and only a precompiled cache in <BinaryItem> -- confirmed
        // against a real .dtsx built by SSDT's own Script Task editor, not guessed.
        var package = DtsxPackageReader.Read(ForEachScriptPath, noRedact: false);
        var loop = Assert.Single(package.Executables);
        var scriptTask = Assert.Single(loop.Children);

        Assert.False(scriptTask.ScriptTask!.SourceStripped);
        var item = Assert.Single(scriptTask.ScriptTask.ProjectItems);
        Assert.Equal("ScriptMain.vb", item.Name);
        Assert.Equal("UTF8", item.Encoding);
        Assert.Contains("Public Class ScriptMain", item.Content);

        var binaryName = Assert.Single(scriptTask.ScriptTask.BinaryItemNames);
        Assert.Equal("ST_d94303efd87041348059ee651fec5022.dll", binaryName);
    }

    [Fact]
    public void ScriptTask_ReadScriptTask_DetectsSourceStrippedFromObjectDataDirectly()
    {
        // Direct unit test rather than a third .dtsx fixture: constructs the one XML shape
        // plan §4.5 asks to be flagged loudly -- a <ScriptProject> whose source was stripped,
        // leaving only the compiled <BinaryItem> cache -- and calls the reader directly via
        // InternalsVisibleTo, the same pattern DagAlgorithmTests uses for BuildDag. Not
        // observed on any real package yet (see ScriptTaskPayload.SourceStripped's own doc
        // comment), so there is no real .dtsx evidence to build a full fixture from.
        // RulesEngineTests covers the resulting "script-source-stripped" finding.
        var objectData = System.Xml.Linq.XElement.Parse("""
            <ObjectData>
              <ScriptProject Name="ST_stripped" VSTAMajorVersion="17" VSTAMinorVersion="0" Language="CSharp">
                <BinaryItem Name="ST_stripped.dll">AAAA</BinaryItem>
              </ScriptProject>
            </ObjectData>
            """);

        var payload = DtsxPackageReader.ReadScriptTask(objectData);

        Assert.True(payload.SourceStripped);
        Assert.Empty(payload.ProjectItems);
        Assert.Equal(["ST_stripped.dll"], payload.BinaryItemNames);
    }

    [Fact]
    public void ScriptTask_And_ForEachLoop_TriggerTheirRewriteEffortRules()
    {
        var package = DtsxPackageReader.Read(ForEachScriptPath, noRedact: false);
        var findings = RulesEngine.Evaluate(package).Select(f => f.RuleId).ToList();

        Assert.Contains("loop-present", findings);
        Assert.Contains("script-task-present", findings);
    }

    [Fact]
    public void UnusedVariableRule_DoesNotFlagAForEachLoopsOwnIterationVariable()
    {
        // Regression guard for the false positive this fixture caught: before RulesEngine's
        // expression-text collector knew about ForEachLoop.VariableMappings/ScriptTask's
        // read/write variable lists, User::CurrentFile was wrongly reported unused even
        // though it's the loop's own per-iteration value, consumed by the Script Task.
        var package = DtsxPackageReader.Read(ForEachScriptPath, noRedact: false);
        var unusedVariableMessages = RulesEngine.Evaluate(package)
            .Where(f => f.RuleId == "unused-variable")
            .Select(f => f.Message)
            .ToList();

        Assert.DoesNotContain(unusedVariableMessages, m => m.Contains("User::CurrentFile"));
        // FolderPath genuinely IS unused in this fixture (set as a literal enumerator
        // property, never bound via a @[User::FolderPath] expression) -- still expected.
        Assert.Contains(unusedVariableMessages, m => m.Contains("User::FolderPath"));
    }

    [Fact]
    public void LookupSplit_ExtractsAt100PercentCoverage()
    {
        var package = DtsxPackageReader.Read(LookupSplitPath, noRedact: false);
        Assert.Equal(100.0, package.Coverage.CoveragePercent);
    }

    [Fact]
    public void Lookup_ConnectionSqlCommandAndOutputs_ParseCorrectly()
    {
        var package = DtsxPackageReader.Read(LookupSplitPath, noRedact: false);
        var dataFlow = Assert.Single(package.Executables).DataFlowTask!;
        var lookup = dataFlow.Pipeline.Components.Single(c => c.ComponentClassId == "Microsoft.Lookup");

        Assert.NotNull(lookup.Lookup);
        Assert.Equal("CM_SyntheticDb", lookup.Lookup!.ConnectionName);
        Assert.Equal("SELECT CustomerID, CustomerName, Region FROM dbo.SyntheticCustomer", lookup.Lookup.SqlCommand);
        Assert.Equal(1, lookup.Lookup.NoMatchBehaviorRaw);
        Assert.Equal("Lookup Match Output", lookup.Lookup.MatchOutputName);
        Assert.Equal("Lookup No Match Output", lookup.Lookup.NoMatchOutputName);
    }

    [Fact]
    public void Lookup_ReferenceColumns_ParsedFromReferenceMetadataXml()
    {
        var package = DtsxPackageReader.Read(LookupSplitPath, noRedact: false);
        var dataFlow = Assert.Single(package.Executables).DataFlowTask!;
        var lookup = dataFlow.Pipeline.Components.Single(c => c.ComponentClassId == "Microsoft.Lookup");

        var names = lookup.Lookup!.ReferenceColumns.Select(c => c.Name).ToList();
        Assert.Equal(["CustomerID", "CustomerName", "Region"], names);
        Assert.Equal("DT_I4", lookup.Lookup.ReferenceColumns.Single(c => c.Name == "CustomerID").DataType);
    }

    [Fact]
    public void ConditionalSplit_CaseAndDefaultOutput_ParseCorrectly()
    {
        var package = DtsxPackageReader.Read(LookupSplitPath, noRedact: false);
        var dataFlow = Assert.Single(package.Executables).DataFlowTask!;
        var split = dataFlow.Pipeline.Components.Single(c => c.ComponentClassId == "Microsoft.ConditionalSplit");

        Assert.NotNull(split.ConditionalSplit);
        Assert.Equal("LowValue", split.ConditionalSplit!.DefaultOutputName);
        var highValueCase = Assert.Single(split.ConditionalSplit.Cases);
        Assert.Equal("HighValue", highValueCase.OutputName);
        Assert.Equal("Amount > 1000", highValueCase.FriendlyExpression);
        Assert.Equal(0, highValueCase.EvaluationOrder);
    }

    [Fact]
    public void OleDbSource_SqlCommandAndColumnMappings_ParseCorrectly()
    {
        var package = DtsxPackageReader.Read(LookupSplitPath, noRedact: false);
        var dataFlow = Assert.Single(package.Executables).DataFlowTask!;
        var source = dataFlow.Pipeline.Components.Single(c => c.ComponentClassId == "Microsoft.OLEDBSource");

        Assert.NotNull(source.OleDbSource);
        Assert.Equal("SELECT OrderID, CustomerID, Amount FROM dbo.SyntheticSourceOrder", source.OleDbSource!.SqlCommand);
        Assert.Equal(2, source.OleDbSource.AccessMode);
        Assert.Equal(3, source.OleDbSource.ColumnMappings.Count);
        Assert.Contains(source.OleDbSource.ColumnMappings, m => m.ComponentColumnName == "OrderID" && m.ExternalColumnName == "OrderID");
    }

    [Fact]
    public void LookupAndConditionalSplit_StaySynchronous_NoFalseAsyncFinding()
    {
        // Both components' outputs carry synchronousInputId in the real fixture (confirmed
        // from the object model's own saved XML) -- this guards against the generic
        // "async-transform-present" rule wrongly firing on either.
        var package = DtsxPackageReader.Read(LookupSplitPath, noRedact: false);
        var findings = RulesEngine.Evaluate(package).Select(f => f.RuleId).ToList();

        Assert.DoesNotContain("async-transform-present", findings);
    }
}
