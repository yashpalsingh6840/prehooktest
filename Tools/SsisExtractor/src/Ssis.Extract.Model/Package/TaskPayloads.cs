using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Model.Package;

/// <summary>
/// <c>Microsoft.ExecuteSQLTask</c> (plan §4.5 table). The one task type both this PoC's
/// packages actually use, so <see cref="SqlStatementSource"/>/<see cref="ConnectionName"/>
/// are evidenced; <see cref="ParameterBindings"/>/<see cref="ResultBindings"/> are
/// best-effort (neither PoC package's Execute SQL Task uses parameters or result sets) --
/// verify the exact attribute names against a real client package before trusting those
/// two lists on anything but "empty means none used."
/// </summary>
public sealed class ExecuteSqlTaskPayload
{
    public string? SqlStatementSource { get; init; }

    /// <summary>DirectInput/FileConnection/Variable. Null means the schema default (DirectInput) applies.</summary>
    public string? SqlStatementSourceType { get; init; }

    /// <summary>The connection manager's DTSID as referenced by this task (raw GUID form, e.g. "{...}") -- a different reference shape from every other cross-reference in this model, which use refId paths.</summary>
    public string? ConnectionRefRaw { get; init; }

    /// <summary>Resolved from <see cref="ConnectionRefRaw"/> against this package's own connection managers by DtsId. Null if unresolvable -- a dangling reference, or (not possible in the Project-Deployment-Model packages here, but conceivable elsewhere) a connection manager outside this package's own list.</summary>
    public string? ConnectionName { get; init; }

    public bool? IsStoredProc { get; init; }
    public string? ResultSetType { get; init; }
    public int? TimeOut { get; init; }
    public int? CodePage { get; init; }
    public bool? BypassPrepare { get; init; }

    public List<SqlParameterBindingSpec> ParameterBindings { get; init; } = [];
    public List<SqlResultBindingSpec> ResultBindings { get; init; } = [];
}

public sealed class SqlParameterBindingSpec
{
    public string? DtsVariableName { get; init; }
    public string? ParameterName { get; init; }
    public string? Direction { get; init; }
    public int? DataTypeRaw { get; init; }
    public int? ParameterSize { get; init; }
}

public sealed class SqlResultBindingSpec
{
    public string? ResultName { get; init; }
    public string? DtsVariableName { get; init; }
}

/// <summary>
/// <c>Microsoft.Pipeline</c> (Data Flow Task, plan §4.7). Slice 2 kept this as an opaque
/// raw-XML marker; slice 3 parses it fully into <see cref="Pipeline"/> (every component,
/// generically, plus bespoke semantics for the types this PoC has evidence for) and derives
/// <see cref="Lineage"/> from it (plan §5.1) -- column-level source-to-target tracing.
/// </summary>
public sealed class DataFlowTaskPayload
{
    public required PipelineSpec Pipeline { get; init; }
    public required LineageSpec Lineage { get; init; }
}

/// <summary>
/// Any executable type without a typed payload above -- the plan's mandatory "anything
/// else" row (§4.5): on a portfolio of unknown packages you *will* meet a task type this
/// build slice didn't anticipate, and it must be reported loudly (via the coverage
/// percentage) rather than silently skipped.
/// </summary>
public sealed class UnmappedTaskPayload
{
    public required string RawObjectDataXml { get; init; }
}
