namespace Ssis.Extract.Model.Analysis;

/// <summary>
/// Semantic diff between two versions of the same package (plan §6.2's <c>ssisx diff</c>) --
/// e.g. source control vs what is actually deployed. Deliberately <b>not</b> a text diff of
/// the two spec.json files: those already diff cleanly (deterministic output, plan §2.3),
/// but a text diff can't tell "this package gained a task" from "this package's designer
/// layout moved". This compares the typed model at a semantic level and reports changes in
/// the things a migration actually cares about.
/// </summary>
public sealed class PackageDiffSpec
{
    public required string LeftPackageName { get; init; }
    public required string RightPackageName { get; init; }
    public required string LeftSha256 { get; init; }
    public required string RightSha256 { get; init; }

    /// <summary>True when the two files are byte-identical -- every other list below is then necessarily empty, and this is the fast answer to "has this drifted at all".</summary>
    public required bool Identical { get; init; }

    public List<PackageDiffEntry> Differences { get; init; } = [];
}

/// <summary>One semantic difference. <see cref="Area"/> groups them so a reader can tell "the control flow changed" from "a connection string changed" at a glance.</summary>
public sealed class PackageDiffEntry
{
    /// <summary>"Executables" | "ConnectionManagers" | "Variables" | "Parameters" | "PrecedenceConstraints" | "Pipeline" | "PackageProperties".</summary>
    public required string Area { get; init; }

    /// <summary>"Added" | "Removed" | "Changed".</summary>
    public required string Change { get; init; }

    /// <summary>What changed -- a refId, name, or property path, whichever identifies the object in its own area.</summary>
    public required string Identity { get; init; }

    /// <summary>Populated for "Changed" only.</summary>
    public string? LeftValue { get; init; }

    public string? RightValue { get; init; }
}
