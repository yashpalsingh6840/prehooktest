namespace Etl.Core.Hosting;

/// <summary>One named Flat File Source: the folder/filename pair SSIS would have carried as a
/// pair of project/package parameters.</summary>
public sealed class FileSourceEntry
{
    public required string SourceFolder { get; init; }
    public required string SourceFileName { get; init; }

    public string ResolvedPath => Path.Combine(SourceFolder, SourceFileName);
}

/// <summary>
/// Replaces SSIS's SourceFolder/SourceFileName project and package parameters.
/// </summary>
/// <remarks>
/// An earlier version of this type carried exactly one <see cref="FileSourceEntry"/>'s worth of
/// properties directly, so a package needing more than one Flat File Source (LoadReferenceData,
/// with Department + Designation) had no way to use it and invented its own bespoke options type
/// instead -- exactly the per-package divergence this skeleton exists to close. This version
/// holds any number of named entries, keyed by whatever name a package's Program.cs chooses
/// (e.g. "Employees", or "Department"/"Designation"), so every package binds the same
/// "FileSource" configuration section shape regardless of how many sources it has.
/// </remarks>
public sealed class FileSourceOptions
{
    public Dictionary<string, FileSourceEntry> Files { get; init; } = new();

    public FileSourceEntry this[string name] =>
        Files.TryGetValue(name, out var entry)
            ? entry
            : throw new KeyNotFoundException(
                $"No FileSource entry named '{name}' is configured. Configured entries: " +
                string.Join(", ", Files.Keys));
}
