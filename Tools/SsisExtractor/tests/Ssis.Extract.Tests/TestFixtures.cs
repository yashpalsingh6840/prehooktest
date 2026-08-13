using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Tests;

/// <summary>Minimal-but-valid <see cref="PackageSpec"/> builder shared by the analysis-layer unit tests (<c>ComplexityScorerTests</c>, <c>RulesEngineTests</c>, <c>NonDeterminismAnalyzerTests</c>) -- fills in only the fields those tests actually vary, defaulting everything else to values that wouldn't trip any rule by themselves.</summary>
internal static class TestFixtures
{
    public static PackageSpec MinimalPackage(string name, string sha256 = "0000000000000000000000000000000000000000000000000000000000000000") => new()
    {
        ObjectName = name,
        SourceDtsxPath = $"{name}.dtsx",
        Sha256 = sha256,
        FileSizeBytes = 1,
        LastWriteTimeUtc = DateTime.UnixEpoch,
        ProtectionLevelRaw = 0,
        ProtectionLevelName = "DontSaveSensitive",
        Coverage = new CoverageStats { TotalElements = 1, UnmappedElements = 0, ExcludedElements = 0, CoveragePercent = 100 },
    };
}
