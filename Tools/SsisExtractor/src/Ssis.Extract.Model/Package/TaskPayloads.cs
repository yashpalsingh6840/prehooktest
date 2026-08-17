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

/// <summary>
/// <c>Microsoft.ScriptTask</c> (VSTA-hosted, the only kind SSIS 17 creates -- confirmed via
/// the synthetic component-coverage fixture, docs/report-schema.md). Note what's genuinely
/// absent: this component's saved &lt;ObjectData&gt; carries no per-task entry-point name
/// (no <c>EntryPoint</c>/<c>EntryPointSymbol</c> attribute) -- confirmed empirically, not an
/// oversight here. The object model's own <c>TaskHost.Properties["EntryPoint"]</c> defaults
/// to <c>"Main"</c> at design time but that value is not persisted to the .dtsx; the actual
/// script source (and therefore its real entry point) lives inside the embedded VSTA project
/// binary this reader does not unpack (plan's own "Script Task source extraction" is listed
/// as still unbuilt) -- this payload only ever describes the task's declared surface
/// (language, which variables it reads/writes), never its behavior.
/// </summary>
public sealed class ScriptTaskPayload
{
    /// <summary>e.g. "VisualBasic" -- the &lt;ScriptProject&gt; element's own <c>Language</c> attribute, confirmed real from the object model (default project template on this machine is VB, not C#).</summary>
    public string? Language { get; init; }

    public string? ProjectName { get; init; }
    public string? VstaMajorVersion { get; init; }
    public string? VstaMinorVersion { get; init; }

    /// <summary>Split from the semicolon-delimited <c>ReadOnlyVariables</c> attribute (e.g. "User::CurrentFile") -- empty when the task declares none.</summary>
    public List<string> ReadOnlyVariables { get; init; } = [];

    /// <summary>Split from the semicolon-delimited <c>ReadWriteVariables</c> attribute.</summary>
    public List<string> ReadWriteVariables { get; init; } = [];
}

/// <summary>
/// <c>STOCK:FOREACHLOOP</c> -- confirmed real via the synthetic component-coverage fixture
/// (docs/report-schema.md). Unlike every other container in this model, a ForEach Loop's
/// enumerator config and variable mappings are NOT under the executable's &lt;ObjectData&gt;
/// (which this component doesn't even have) -- they're two separate sibling elements,
/// &lt;ForEachEnumerator&gt; and &lt;ForEachVariableMappings&gt;, alongside &lt;Executables&gt;.
/// Before this payload existed those two elements were invisible to DtsxPackageReader
/// entirely (its ObjectData branch never ran for this executable type) -- not counted as
/// unmapped, not captured as raw XML, just silently absent from both the model and the
/// coverage percentage's numerator and denominator's accounting of what's "handled". Fixed
/// here, not just added: see DtsxPackageReader.ReadExecutable's own comment on this.
/// </summary>
public sealed class ForEachLoopPayload
{
    /// <summary>e.g. "Microsoft.ForEachFileEnumerator" -- the &lt;ForEachEnumerator&gt;'s own <c>CreationName</c>. Bespoke parsing below only covers the File enumerator (the type this PoC has real evidence for); any other enumerator type still gets this name plus <see cref="RawEnumeratorObjectDataXml"/>, never silently dropped.</summary>
    public string? EnumeratorCreationName { get; init; }

    public ForEachFileEnumeratorSpec? FileEnumerator { get; init; }

    /// <summary>Raw fallback for any enumerator type other than File (Database/Item/Event/ADO/etc.) -- same "unmapped bag" honesty pattern used elsewhere in this reader, just scoped to one sub-element instead of a whole task.</summary>
    public string? RawEnumeratorObjectDataXml { get; init; }

    public List<ForEachVariableMappingSpec> VariableMappings { get; init; } = [];
}

/// <summary><c>&lt;ForEachFileEnumeratorProperties&gt;</c>'s &lt;FEFEProperty&gt; children, each a single-attribute element (confirmed real shape: <c>&lt;FEFEProperty Folder="..."/&gt;</c>, not a generic name/value pair) -- one field here per attribute name actually observed.</summary>
public sealed class ForEachFileEnumeratorSpec
{
    public string? Folder { get; init; }
    public string? FileSpec { get; init; }

    /// <summary>Raw <c>FileNameRetrievalType</c> integer (0 = fully qualified name, on the synthetic fixture's default) -- not otherwise decoded, same raw-enum caveat used elsewhere in this model.</summary>
    public int? FileNameRetrievalTypeRaw { get; init; }

    public bool? Recurse { get; init; }
}

/// <summary>One &lt;ForEachVariableMapping&gt; -- which variable receives the enumerator's value at each iteration. <see cref="ValueIndex"/> is almost always 0 for a File enumerator (there's only one value per file); enumerators with multiple columns (e.g. ADO) use higher indexes, unevidenced here.</summary>
public sealed class ForEachVariableMappingSpec
{
    public required string VariableName { get; init; }
    public int? ValueIndex { get; init; }
}
