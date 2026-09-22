using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Loads the real PoC packages the same way GoldenFileTests does (referenced by relative
/// path, not copied) so these emitter tests run against real .dtsx content, not a hand-rolled
/// PipelineComponentSpec that might not match what the extractor actually produces.
/// </summary>
internal static class TestFixtures
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static readonly string TestsProjectDir = Path.GetDirectoryName(ThisFilePath())!;

    // Tools/SsisExtractor/tests/Ssis.Extract.Codegen.Tests -> repo root -> SSIS/ (the PoC project)
    private static readonly string FixturesDir =
        Path.GetFullPath(Path.Combine(TestsProjectDir, "..", "..", "..", "..", "..", "SSIS_Packages", "SSIS"));

    public static PackageSpec LoadPackage(string dtsxFileName) =>
        DtsxPackageReader.Read(Path.Combine(FixturesDir, dtsxFileName), noRedact: false);

    public static PipelineSpec FindPipeline(PackageSpec package, string dataFlowTaskName) =>
        FindExecutable(package.Executables, dataFlowTaskName)?.DataFlowTask?.Pipeline
            ?? throw new InvalidOperationException($"No Data Flow Task named '{dataFlowTaskName}' found in {package.ObjectName}.");

    private static ExecutableSpec? FindExecutable(IEnumerable<ExecutableSpec> executables, string objectName)
    {
        foreach (var ex in executables)
        {
            if (ex.ObjectName == objectName) return ex;
            var nested = FindExecutable(ex.Children, objectName);
            if (nested is not null) return nested;
        }
        return null;
    }

    public static PipelineComponentSpec FindComponent(PackageSpec package, string componentClassId, string componentName)
    {
        foreach (var pipeline in AllPipelines(package.Executables))
        {
            var match = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == componentClassId && c.Name == componentName);
            if (match is not null) return match;
        }
        throw new InvalidOperationException($"No '{componentClassId}' component named '{componentName}' found in {package.ObjectName}.");
    }

    public static ConnectionManagerSpec FindConnectionManager(PackageSpec package, string objectName) =>
        package.ConnectionManagers.FirstOrDefault(cm => cm.ObjectName == objectName)
            ?? throw new InvalidOperationException($"No connection manager named '{objectName}' found in {package.ObjectName}.");

    private static IEnumerable<PipelineSpec> AllPipelines(IEnumerable<ExecutableSpec> executables)
    {
        foreach (var ex in executables)
        {
            if (ex.DataFlowTask is not null) yield return ex.DataFlowTask.Pipeline;
            foreach (var nested in AllPipelines(ex.Children)) yield return nested;
        }
    }
}
