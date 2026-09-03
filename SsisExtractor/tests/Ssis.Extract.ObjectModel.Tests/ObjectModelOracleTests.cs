using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using Ssis.Extract.ObjectModel;

namespace Ssis.Extract.ObjectModel.Tests;

/// <summary>
/// Plan §7.3's test oracle: loads a package both ways and asserts they agree on the
/// structural facts the object model can independently confirm. "Both ways" here means the
/// real object model (<see cref="PackageOracle"/>) versus the *already-golden-tested*
/// XML-derived spec.json committed at <c>Ssis.Extract.Tests/Golden</c> -- read generically
/// via <c>JObject</c> rather than deserialized into <c>PackageSpec</c>, since that model
/// lives in a net8.0-only project this net48 test project cannot reference (a .NET
/// Framework runtime cannot load a .NET 8 assembly). This is a deliberate, one-way file-based
/// contract between the two runtimes, not a limitation worked around silently.
///
/// Windows-only, optional, not part of <c>SsisExtractor.slnx</c>'s default build -- run with
/// <c>dotnet test tests/Ssis.Extract.ObjectModel.Tests</c> specifically, on a machine with
/// the SSIS 17 GAC assemblies (see the .csproj's own HintPaths).
/// </summary>
public class ObjectModelOracleTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;
    private static readonly string TestsDir = Path.GetDirectoryName(ThisFilePath())!;

    // tests/Ssis.Extract.ObjectModel.Tests -> Tools/SsisExtractor -> repo root -> SSIS/
    private static readonly string PoCPackagesDir =
        Path.GetFullPath(Path.Combine(TestsDir, "..", "..", "..", "..", "SSIS"));

    private static readonly string GoldenDir =
        Path.GetFullPath(Path.Combine(TestsDir, "..", "Ssis.Extract.Tests", "Golden"));

    private static readonly string SyntheticFixturesDir =
        Path.GetFullPath(Path.Combine(TestsDir, "..", "Ssis.Extract.Tests", "Fixtures"));

    [Theory]
    [InlineData("LoadEmployees.dtsx", "LoadEmployees.spec.json")]
    [InlineData("LoadReferenceData.dtsx", "LoadReferenceData.spec.json")]
    public void ObjectModelFacts_AgreeWithXmlDerivedGoldenSpec(string dtsxFileName, string goldenFileName)
    {
        var oracleFacts = PackageOracle.Load(Path.Combine(PoCPackagesDir, dtsxFileName));
        var golden = JObject.Parse(File.ReadAllText(Path.Combine(GoldenDir, goldenFileName)));
        var jsonFacts = ComputeFactsFromSpecJson(golden);

        Assert.Equal(jsonFacts.ExecutableCount, oracleFacts.ExecutableCount);
        Assert.Equal(jsonFacts.ExecutableNames.OrderBy(n => n, StringComparer.Ordinal), oracleFacts.ExecutableNames.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(jsonFacts.ConnectionManagerCount, oracleFacts.ConnectionManagerCount);
        Assert.Equal(jsonFacts.ConnectionManagerCreationNames, oracleFacts.ConnectionManagerCreationNames);
        Assert.Equal(jsonFacts.VariableCount, oracleFacts.VariableCount);
        Assert.Equal(jsonFacts.ParameterCount, oracleFacts.ParameterCount);
        Assert.Equal(jsonFacts.ParameterDataTypeNames, oracleFacts.ParameterDataTypeNames);
        Assert.Equal(jsonFacts.PipelineComponentCount, oracleFacts.PipelineComponentCount);
        Assert.Equal(jsonFacts.PipelinePathCount, oracleFacts.PipelinePathCount);
        Assert.Equal(jsonFacts.PrecedenceConstraintCount, oracleFacts.PrecedenceConstraintCount);
    }

    /// <summary>
    /// Neither real PoC package nests a container (both are straight-line task lists), so
    /// the oracle's recursive walk into <c>IDTSSequence.Executables</c> is otherwise
    /// unexercised by the test above -- same "proven synthetically, not by the PoC's own
    /// packages" pattern as <c>DagAlgorithmTests</c>. No committed golden JSON exists for
    /// this fixture, so this asserts hand-verified expected values directly rather than
    /// cross-checking against a second independently-computed side.
    /// </summary>
    [Fact]
    public void ObjectModelFacts_RecurseIntoForEachLoopContainer()
    {
        var facts = PackageOracle.Load(Path.Combine(SyntheticFixturesDir, "SyntheticForEachScript.dtsx"));

        Assert.Equal(2, facts.ExecutableCount); // FELC_Files + its nested SCR_LogFile
        Assert.Equal(new[] { "FELC_Files", "SCR_LogFile" }, facts.ExecutableNames.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(2, facts.VariableCount); // User::FolderPath, User::CurrentFile, both package-level
        Assert.Equal(0, facts.ConnectionManagerCount);
        Assert.Equal(0, facts.ParameterCount);
        Assert.Equal(0, facts.PipelineComponentCount);
        Assert.Equal(0, facts.PrecedenceConstraintCount);
    }

    /// <summary>Same rationale as the ForEach Loop test above, but for the pipeline-heavy fixture -- neither PoC package's single Data Flow Task has more than 3 components, so a 6-component/5-path pipeline (Source -> Lookup -> Conditional Split -> 3 Destinations) is otherwise unexercised.</summary>
    [Fact]
    public void ObjectModelFacts_CountPipelineComponentsAndPathsAcrossABranchingDataFlow()
    {
        var facts = PackageOracle.Load(Path.Combine(SyntheticFixturesDir, "SyntheticLookupSplit.dtsx"));

        Assert.Equal(1, facts.ExecutableCount); // DFT_LookupSplitDemo, no nesting
        Assert.Equal(1, facts.ConnectionManagerCount);
        Assert.Equal(6, facts.PipelineComponentCount); // Source, Lookup, Conditional Split, 3x Destination
        Assert.Equal(5, facts.PipelinePathCount);
    }

    /// <summary>
    /// Mirrors <see cref="PackageOracle"/>'s own recursive counting rules exactly (same
    /// "every node contributes its own Variables/PrecedenceConstraints, walk every Children
    /// list, find every DataFlowTask at any depth" shape) so the two are actually comparable,
    /// not coincidentally equal for these two straight-line packages.
    /// </summary>
    private static PackageOracleFacts ComputeFactsFromSpecJson(JObject package)
    {
        var facts = new PackageOracleFacts
        {
            VariableCount = ((JArray)package["Variables"]!).Count,
            PrecedenceConstraintCount = ((JArray)package["PrecedenceConstraints"]!).Count,
            ConnectionManagerCreationNames = ((JArray)package["ConnectionManagers"]!)
                .Select(cm => (string)cm["CreationName"]!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList(),
            ParameterDataTypeNames = ((JArray)package["Parameters"]!)
                .Select(p => (string)p["DataTypeName"]!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList(),
        };
        facts.ConnectionManagerCount = facts.ConnectionManagerCreationNames.Count;
        facts.ParameterCount = facts.ParameterDataTypeNames.Count;

        WalkExecutables((JArray)package["Executables"]!, facts);
        return facts;
    }

    private static void WalkExecutables(JArray executables, PackageOracleFacts facts)
    {
        foreach (var exToken in executables)
        {
            var ex = (JObject)exToken;
            facts.ExecutableCount++;
            facts.ExecutableNames.Add((string)ex["ObjectName"]!);
            facts.VariableCount += ((JArray)ex["Variables"]!).Count;
            facts.PrecedenceConstraintCount += ((JArray)ex["PrecedenceConstraints"]!).Count;

            if (ex["DataFlowTask"] is JObject dataFlowTask)
            {
                var pipeline = (JObject)dataFlowTask["Pipeline"]!;
                facts.PipelineComponentCount += ((JArray)pipeline["Components"]!).Count;
                facts.PipelinePathCount += ((JArray)pipeline["Paths"]!).Count;
            }

            WalkExecutables((JArray)ex["Children"]!, facts);
        }
    }
}
