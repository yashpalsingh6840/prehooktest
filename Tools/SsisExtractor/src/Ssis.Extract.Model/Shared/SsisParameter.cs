namespace Ssis.Extract.Model.Shared;

/// <summary>
/// A project or package parameter (Project-Deployment-Model). Same shape at both scopes;
/// <see cref="Scope"/> records which. Source: <c>Project.params</c> / <c>.dtproj</c>
/// manifest for project scope, <c>DTS:PackageParameters</c> inside a <c>.dtsx</c> for
/// package scope (see CLAUDE.md: LoadReferenceData.dtsx is the package-scope example).
/// </summary>
public sealed class SsisParameter
{
    public required string Name { get; init; }
    public string? Id { get; init; }
    public string? CreationName { get; init; }
    public string? Description { get; init; }
    public int? IncludeInDebugDump { get; init; }
    public bool Required { get; init; }
    public bool Sensitive { get; init; }

    /// <summary>
    /// Design-time value as persisted in the source file. Null when the parameter is
    /// sensitive and the .ispac/build already strips it (CLAUDE.md trap 13: a sensitive
    /// parameter's design_default_value is NULL by design), or when the source simply
    /// carries no value.
    /// </summary>
    public string? Value { get; init; }

    public required int DataTypeRaw { get; init; }
    public required string DataTypeName { get; init; }

    /// <summary>"Project" or "Package".</summary>
    public required string Scope { get; init; }
}
