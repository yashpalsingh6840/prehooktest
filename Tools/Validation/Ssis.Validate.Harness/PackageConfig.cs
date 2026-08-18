namespace Ssis.Validate.Harness;

/// <summary>
/// One package's gate-3 config -- plan §12's "one generated config per package". JSON, not
/// YAML as the plan's prose illustrates: this repo has no YAML dependency anywhere and
/// StableJsonWriter-style JSON is already the convention for every other generated artifact
/// (spec.json, conformance rules, the non-determinism manifest itself) -- an intentional,
/// documented deviation from the plan's literal wording, not a different design.
/// </summary>
public sealed class PackageConfig
{
    public required string PackageName { get; init; }

    /// <summary>The DDL script that creates this package's target schema, applied fresh before every gate-3 run (plan §5 step 1). Path is relative to the repo root.</summary>
    public required string SchemaScript { get; init; }

    public List<TargetTableConfig> TargetTables { get; init; } = [];
}

public sealed class TargetTableConfig
{
    public required string Table { get; init; }

    /// <summary>Used to order snapshot rows deterministically and to match rows during diff. GENERATED configs leave this null (the extractor doesn't track primary keys yet) -- a human fills it in from the schema, same as the plan's own "reviewed by a human" framing.</summary>
    public string? KeyColumn { get; init; }

    /// <summary>From the non-determinism manifest (plan §5.8) -- populated automatically by the generator, never hand-typed.</summary>
    public List<string> ExcludedColumns { get; init; } = [];
}
