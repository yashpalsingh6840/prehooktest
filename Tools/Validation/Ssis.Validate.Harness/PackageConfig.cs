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

    /// <summary>Extra setup scripts run, in order, against the real target connection string right after <see cref="SchemaScript"/> -- for state <see cref="SchemaScript"/> itself can't own, such as a job's SQL-auth login/grants (LoadEmployees' ssis_loader). Needed because a script that DROPs/recreates a table (as SchemaScript does) wipes any GRANTs a prior run of a credential script made on that same object, so the credential script must be re-applied every time, after the table exists again -- not folded into SchemaScript itself, which stays a pure schema DDL script shared with the rest of this repo (Sql/01_CreateTarget.sql). Empty for packages with no such setup (e.g. LoadReferenceData, Windows-auth only).</summary>
    public List<string> AdditionalSchemaScripts { get; init; } = [];

    public List<TargetTableConfig> TargetTables { get; init; } = [];

    /// <summary>
    /// Every real-world effect `ssisx report`'s effects.json named for this package that is
    /// NOT one of <see cref="TargetTables"/> -- a file drop, a stored procedure whose own
    /// writes aren't enumerated here, or a task type the extractor couldn't characterize at
    /// all. Snapshotted at config-generation time, the same pattern as
    /// <see cref="TargetTableConfig.ExcludedColumns"/>.
    ///
    /// <para><b>This is the field that stops a false PASS.</b> Without it, a package whose
    /// real output is (say) an email would have zero TargetTables, nothing to diff, and
    /// GateRunner would report success on an empty comparison. Every entry here whose
    /// <see cref="DeclaredEffectConfig.Verifiability"/> is not <c>"Verifiable"</c> forces the
    /// run's outcome to <c>Incomplete</c> instead of <c>Pass</c> -- see GateRunner.Run.</para>
    /// </summary>
    public List<DeclaredEffectConfig> DeclaredEffects { get; init; } = [];
}

/// <summary>One row of `ssisx report`'s effects.json, kept for a package after its SqlTable effects are folded into <see cref="PackageConfig.TargetTables"/> instead.</summary>
public sealed class DeclaredEffectConfig
{
    public required string Kind { get; init; }
    public required string Target { get; init; }

    /// <summary>"Verifiable" | "NeedsChecker" | "NeedsHumanReview" -- copied verbatim from effects.json at generation time. Anything other than "Verifiable" blocks a Pass outcome.</summary>
    public required string Verifiability { get; init; }

    public required string Note { get; init; }
}

public sealed class TargetTableConfig
{
    public required string Table { get; init; }

    /// <summary>Used to order snapshot rows deterministically and to match rows during diff. GENERATED configs leave this null (the extractor doesn't track primary keys yet) -- a human fills it in from the schema, same as the plan's own "reviewed by a human" framing.</summary>
    public string? KeyColumn { get; init; }

    /// <summary>From the non-determinism manifest (plan §5.8) -- populated automatically by the generator, never hand-typed.</summary>
    public List<string> ExcludedColumns { get; init; } = [];
}
