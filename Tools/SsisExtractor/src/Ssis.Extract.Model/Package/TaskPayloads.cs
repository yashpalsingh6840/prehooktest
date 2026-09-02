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
/// <c>Microsoft.FileSystemTask</c>. Confirmed via the real object model before any XML was
/// hand-written (same "ask the runtime, don't guess" discipline as trap 12), not just read off
/// a single real package's own saved XML: <see cref="OperationRaw"/> is OMITTED from
/// <c>&lt;FileSystemData&gt;</c> when it's <c>CopyFile</c> (the enum's default value, confirmed
/// by building a probe package with the operation left unset and reading the saved XML back --
/// no <c>TaskOperationType</c> attribute at all), and present as the literal enum name
/// (<c>"MoveFile"</c>, <c>"DeleteFile"</c>, ...) otherwise. <see cref="SourcePathRaw"/>/
/// <see cref="DestinationPathRaw"/> can each independently be a literal path, a
/// <c>Namespace::VariableName</c> reference (when <see cref="SourceIsVariable"/>/
/// <see cref="DestinationIsVariable"/> is true), or -- the shape RBC_Demo_ETL's own real
/// FST_ArchiveWorkbook uses -- a FILE connection manager's DTSID, resolved the same way
/// <see cref="ExecuteSqlTaskPayload.ConnectionRefRaw"/>/<c>ConnectionName</c> already are.
/// </summary>
public sealed class FileSystemTaskPayload
{
    /// <summary>e.g. "MoveFile", "DeleteFile" -- null (not "CopyFile") when the schema default applies; see this type's own doc comment.</summary>
    public string? OperationRaw { get; init; }

    public string? SourcePathRaw { get; init; }
    public bool? SourceIsVariable { get; init; }

    /// <summary>Resolved from <see cref="SourcePathRaw"/> against this package's own connection managers by DtsId, same as <see cref="ExecuteSqlTaskPayload.ConnectionName"/> -- null when the source isn't a variable and doesn't resolve to a known connection manager (a literal path, or a dangling reference).</summary>
    public string? SourceConnectionName { get; init; }

    public string? DestinationPathRaw { get; init; }
    public bool? DestinationIsVariable { get; init; }
    public string? DestinationConnectionName { get; init; }

    public bool? OverwriteDestination { get; init; }
}

/// <summary>
/// <c>Microsoft.ExecutePackageTask</c> -- runs another package (project-reference or legacy
/// file/MSDB reference) as a child of the one being extracted. Extraction-only for now:
/// <c>ssisx generate</c> reports a named, honest structural gap for this task type rather than
/// attempting to compose two generated projects together, a genuine architectural decision
/// (in-process reference vs. shell-out vs. macro-flatten) deferred until a real client package
/// using this task exists to decide it against -- see <c>PackagePlanner</c>'s own handling.
///
/// <b>Verified against the real SSIS 16.0/2022 object model on this machine</b> (a live
/// build-and-save-XML round trip, not guessed from documentation alone -- the same "ask the
/// runtime, don't guess" discipline as trap 12), because the two written specs disagree with
/// each other in a way that matters: the older MS-DTSX open spec (an SSIS ~2008-era document)
/// describes <c>ExecutableType="SSIS.ExecutePackageTask.2"</c>/<c>"STOCK:ExecutePackageTask"</c>
/// and a <c>PackageID</c>/<c>VersionID</c>/<c>Connection</c>-keyed file/MSDB reference shape with
/// no project-reference concept at all (that mode postdates the 2012 Project Deployment Model);
/// Microsoft's own current property-grid docs describe a
/// <c>PackageNameFromProjectReference</c> UI property that turned out NOT to be a real, separate
/// persisted field. What is actually saved on this version: <c>ExecutableType</c>/
/// <c>CreationName</c> are both literally <c>"Microsoft.ExecutePackageTask"</c>; the unqualified
/// (no <c>DTS:</c> prefix) <c>&lt;ExecutePackageTask&gt;</c> wrapper element's children are
/// present only when they differ from their schema default (same omit-the-default convention as
/// <see cref="FileSystemTaskPayload.OperationRaw"/>): <c>&lt;ExecuteOutOfProcess&gt;</c>,
/// <c>&lt;UseProjectReference&gt;</c> (present, "True", ONLY for project-reference mode -- absent,
/// not "False", for the legacy mode), <c>&lt;PackageName&gt;</c> (the SAME element in BOTH modes
/// -- a project-relative package name when <see cref="UseProjectReference"/> is true, or a
/// literal/expression path in legacy mode; "PackageNameFromProjectReference" is purely an SSDT
/// property-grid DISPLAY label for this same field, confirmed by reflecting the real
/// <c>IDTSExecutePackage100</c> COM interface in <c>Microsoft.SqlServer.ExecPackageTaskWrap</c>,
/// which has no such member at all), <c>&lt;PackageID&gt;</c>/<c>&lt;VersionID&gt;</c> (legacy
/// mode only, unset in the probe so their real persisted shape when non-empty is still
/// unconfirmed), and <c>&lt;Connection&gt;</c> (legacy mode only, a connection-manager DTSID
/// reference, resolved the same way <see cref="ExecuteSqlTaskPayload.ConnectionRefRaw"/>/
/// <c>ConnectionName</c> already are).
///
/// <b>Not modeled, deliberately</b>: project-reference parameter bindings
/// (<c>ParameterAssignments</c>, real per Microsoft's own docs and the live object model's
/// <c>IDTSParameterAssignments</c> interface, but never populated in this probe, so its real
/// persisted XML shape is still unconfirmed) -- <see cref="DtsxPackageReader"/> reports
/// <c>coverage.Unmapped</c> for any such element found rather than silently treating it as
/// covered, so a real package using parameter bindings surfaces as a coverage regression
/// instead of a silent gap, until a real example lets this be modeled for real.
/// </summary>
public sealed class ExecutePackageTaskPayload
{
    public bool? ExecuteOutOfProcess { get; init; }

    /// <summary>Present ("True") only for project-reference mode; absent (not "False") for legacy file/MSDB-reference mode -- the real discriminator between the two shapes.</summary>
    public bool? UseProjectReference { get; init; }

    /// <summary>The child package's name -- project-relative (project-reference mode) or a literal/expression path (legacy mode). The SAME underlying field in both modes; see this type's own doc comment.</summary>
    public string? PackageName { get; init; }

    /// <summary>Legacy mode only. Raw GUID text, not resolved against anything -- this project has no catalog of package IDs to resolve it to.</summary>
    public string? PackageIdRaw { get; init; }

    /// <summary>Legacy mode only. Raw GUID text, not resolved against anything.</summary>
    public string? VersionIdRaw { get; init; }

    /// <summary>Legacy mode only. The connection manager's DTSID as referenced by this task, before resolution -- same raw-GUID reference shape as <see cref="ExecuteSqlTaskPayload.ConnectionRefRaw"/>.</summary>
    public string? ConnectionRefRaw { get; init; }

    /// <summary>Resolved from <see cref="ConnectionRefRaw"/> against this package's own connection managers by DtsId, same as <see cref="ExecuteSqlTaskPayload.ConnectionName"/>. Null if unresolvable or not in legacy mode.</summary>
    public string? ConnectionName { get; init; }
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
/// to <c>"Main"</c> at design time but that value is not persisted to the .dtsx; by VSTA
/// convention the entry point is always <c>ScriptMain</c> in a <see cref="ProjectItems"/>
/// entry named "ScriptMain.cs"/"ScriptMain.vb".
///
/// Earlier assumption here was wrong and has been corrected with real evidence (a genuine
/// .dtsx built by SSDT's own Script Task editor, not guessed): the actual script source is
/// NOT inside an opaque compiled binary. It is plain text, verbatim, in one
/// <c>&lt;ProjectItem Name="..." Encoding="..."&gt;CDATA&lt;/ProjectItem&gt;</c> sibling per
/// VSTA project file (ScriptMain.cs/.vb, the .csproj/.vbproj, AssemblyInfo, Resources.resx,
/// Settings.settings, and an internal MSBuild-ish "Project" descriptor) -- see
/// <see cref="ProjectItems"/>. The one thing genuinely opaque is <c>&lt;BinaryItem
/// Name="....dll"&gt;</c>: a base64-encoded precompiled cache of the same source, never the
/// only copy of it -- see <see cref="BinaryItemNames"/>/<see cref="SourceStripped"/>. This
/// payload still only describes the task's declared surface (language, which variables it
/// reads/writes) plus the extracted source text; it never executes or semantically
/// understands the script (plan's own "Script Tasks are extracted, not understood").
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

    /// <summary>
    /// Every &lt;ProjectItem&gt; under &lt;ScriptProject&gt;, verbatim -- the VSTA mini-project's
    /// own source and support files (ScriptMain.cs/.vb, the .csproj/.vbproj, AssemblyInfo.*,
    /// Resources/Settings designer files, and an internal "Project" MSBuild-ish descriptor).
    /// Empty when the task was saved with source stripped -- see <see cref="SourceStripped"/>.
    /// </summary>
    public List<ScriptProjectItemSpec> ProjectItems { get; init; } = [];

    /// <summary>
    /// Names only (e.g. "ST_xxx.dll") of every &lt;BinaryItem&gt; under &lt;ScriptProject&gt; --
    /// a precompiled cache of the same code in <see cref="ProjectItems"/>, deliberately not
    /// captured as content: it is base64 machine code with no independent information value
    /// for a migration/rewrite audience, and would bloat spec.json for no benefit.
    /// </summary>
    public List<string> BinaryItemNames { get; init; } = [];

    /// <summary>
    /// True when this task's &lt;ScriptProject&gt; has a &lt;BinaryItem&gt; (compiled cache) but
    /// no &lt;ProjectItem&gt; at all -- i.e. the source was stripped and only the opaque
    /// compiled binary remains, the one case plan §4.5 calls out to flag loudly rather than
    /// silently extract nothing. Not yet observed on any package this tool has actually read;
    /// SSDT's default behavior keeps source alongside the binary cache.
    /// </summary>
    public bool SourceStripped { get; init; }
}

/// <summary>One &lt;ProjectItem&gt; -- a single VSTA project source/support file, saved verbatim as CDATA text. <see cref="Encoding"/> (e.g. "UTF8", "UTF16LE") describes the *original on-disk file's* encoding, not this XML's -- honor it when writing <see cref="Content"/> back out to a file, or a UTF16LE item (observed on the internal "Project" descriptor) round-trips as the wrong bytes.</summary>
public sealed class ScriptProjectItemSpec
{
    /// <summary>e.g. "ScriptMain.cs" or "Properties\Resources.resx" -- a backslash-separated relative path, not just a bare file name.</summary>
    public required string Name { get; init; }

    public string? Encoding { get; init; }
    public required string Content { get; init; }
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
