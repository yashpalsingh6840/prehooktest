using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Model.Package;

/// <summary>
/// The canonical per-package spec (plan §4.2) -- what <c>packages/&lt;Package&gt;.spec.json</c>
/// serializes. Slice 1 populated identity, provenance, versioning, protection, execution
/// semantics, checkpoints, logging, property expressions, parameters, connection managers,
/// and variables. Slice 2 adds the control-flow tree (<see cref="Executables"/>), its
/// precedence DAG, event handlers, and the coverage percentage. Pipeline/data-flow
/// *content* (components, columns, expressions -- plan §4.7) is still slice 3 work: a
/// Data Flow Task shows up in <see cref="Executables"/> as a recognized node, but its
/// internals stay in <see cref="DataFlowTaskMarker"/>'s raw XML for now.
/// Anything still not typed at all is captured verbatim in <see cref="Unmapped"/> instead
/// of being silently dropped (plan §2.3).
/// </summary>
public sealed class PackageSpec
{
    // --- Identity (plan §4.2) ---
    public required string ObjectName { get; init; }
    public string? Description { get; init; }
    public string? DtsId { get; init; }
    public string RefId { get; init; } = "Package";
    public required string SourceDtsxPath { get; init; }
    public required string Sha256 { get; init; }
    public required long FileSizeBytes { get; init; }
    public required DateTime LastWriteTimeUtc { get; init; }

    // --- Provenance ---
    public string? CreationDate { get; init; }
    public string? CreatorName { get; init; }
    public string? CreatorComputerName { get; init; }

    // --- Versioning ---
    public int? PackageFormatVersion { get; init; }
    public string? LastModifiedProductVersion { get; init; }
    public string? VersionBuild { get; init; }
    public string? VersionGuid { get; init; }
    public string? VersionComment { get; init; }

    public required int ProtectionLevelRaw { get; init; }
    public required string ProtectionLevelName { get; init; }
    public string? PackageType { get; init; }
    public string? LocaleId { get; init; }

    public ExecutionSemanticsSpec ExecutionSemantics { get; init; } = new();
    public CheckpointSpec Checkpoints { get; init; } = new();
    public LoggingSpec Logging { get; init; } = new();

    public List<PropertyExpressionSpec> PropertyExpressions { get; init; } = [];

    /// <summary>Package-scoped parameters only (Project-Deployment-Model package parameters).</summary>
    public List<SsisParameter> Parameters { get; init; } = [];

    public List<ConnectionManagerSpec> ConnectionManagers { get; init; } = [];
    public List<VariableSpec> Variables { get; init; } = [];
    public List<PackageConfigurationSpec> Configurations { get; init; } = [];
    public bool EnableConfigurations { get; init; }

    /// <summary>The package root's direct children -- the top of the control-flow tree (plan §4.5). Each node recurses via its own <see cref="ExecutableSpec.Children"/>.</summary>
    public List<ExecutableSpec> Executables { get; init; } = [];

    /// <summary>Ordering constraints among <see cref="Executables"/> (plan §4.6) -- the package root's own precedence constraints, same shape as any nested container's.</summary>
    public List<PrecedenceConstraintSpec> PrecedenceConstraints { get; init; } = [];

    /// <summary>Derived DAG over <see cref="Executables"/>/<see cref="PrecedenceConstraints"/>.</summary>
    public ControlFlowDagSpec Dag { get; init; } = new();

    public List<EventHandlerSpec> EventHandlers { get; init; } = [];

    /// <summary>The honesty check (plan §7.2) -- see <see cref="CoverageStats"/>.</summary>
    public required CoverageStats Coverage { get; init; }

    /// <summary>Everything the typed model doesn't yet cover, kept verbatim -- see <see cref="UnmappedFragment"/>.</summary>
    public List<UnmappedFragment> Unmapped { get; init; } = [];
}

/// <summary>
/// Execution semantics that must be replicated by any rewrite (plan §4.2) -- silently
/// dropping any of these changes package behaviour, not just performance.
/// </summary>
public sealed class ExecutionSemanticsSpec
{
    public int? MaxConcurrentExecutables { get; init; }
    public int? MaximumErrorCount { get; init; }
    public bool? FailPackageOnFailure { get; init; }
    public bool? DelayValidation { get; init; }
    public string? TransactionOption { get; init; }
    public string? IsolationLevel { get; init; }
    public string? ForceExecutionResult { get; init; }
    public bool? Disable { get; init; }
    public bool? DisableEventHandlers { get; init; }
}

/// <summary>Restart-from-failure semantics (plan §4.2) -- not present in either PoC package.</summary>
public sealed class CheckpointSpec
{
    public string? CheckpointUsage { get; init; }
    public string? CheckpointFileName { get; init; }
    public bool? SaveCheckpoints { get; init; }
}

public sealed class LoggingSpec
{
    public string? LoggingMode { get; init; }
    public List<LogProviderSpec> Providers { get; init; } = [];
}

public sealed class LogProviderSpec
{
    public string? DtsId { get; init; }
    public string? ObjectName { get; init; }
    public string? CreationName { get; init; }
    public string? ConfigString { get; init; }
}

/// <summary>
/// Legacy Package-Deployment-Model configuration (plan §4.2) -- XML file / env var /
/// registry / parent variable / SQL config table. Not present in either PoC package
/// (both are Project-Deployment-Model); modeled now so a client package that still uses
/// this doesn't fall into <see cref="UnmappedFragment"/> unnoticed once slice 2 walks
/// <c>DTS:Configurations</c>. Left with no reader wiring yet -- see CLAUDE.md's own
/// warning that this is "where a huge amount of undocumented environment coupling lives
/// in older packages."
/// </summary>
public sealed class PackageConfigurationSpec
{
    public string? ObjectName { get; init; }
    public string? ConfigurationType { get; init; }
    public string? ConnectionString { get; init; }
    public string? ConfigurationString { get; init; }
}
