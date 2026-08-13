namespace Ssis.Extract.Model.Analysis;

/// <summary>
/// What one package actually touches (plan §5.3) -- every table/view/proc read, every table
/// written, every file path read or written. Rolled up across the portfolio it answers
/// "what does this thing actually touch" and finds the orphans: files nobody consumes,
/// tables nobody reads.
/// </summary>
public sealed class DataTouchSpec
{
    public required string PackageName { get; init; }

    /// <summary>Tables/views read, from both SQL analysis (plan §5.4) and OLE DB Source components' <c>OpenRowset</c>. Distinct, ordinal-sorted.</summary>
    public List<string> TablesRead { get; init; } = [];

    /// <summary>Tables written, from SQL analysis (INSERT/UPDATE/DELETE/MERGE/TRUNCATE targets) and every OLE DB Destination's <c>OpenRowset</c>. Distinct, ordinal-sorted.</summary>
    public List<string> TablesWritten { get; init; } = [];

    public List<string> ProceduresExecuted { get; init; } = [];

    /// <summary>Flat/file connection-manager paths. <b>Read-vs-written cannot be told apart from a connection manager alone</b> -- it's the component using it (Flat File Source vs Destination) that decides, so these are resolved against pipeline component usage where possible; a file connection manager used by no component lands in <see cref="FilePathsUnknownDirection"/> rather than being guessed into one of the two lists.</summary>
    public List<string> FilePathsRead { get; init; } = [];

    public List<string> FilePathsWritten { get; init; } = [];

    public List<string> FilePathsUnknownDirection { get; init; } = [];

    /// <summary>
    /// Connection managers whose path/connection string is computed at run time from a
    /// property expression, as <c>"&lt;name&gt; = &lt;expression&gt;"</c>. <b>These have no
    /// statically-knowable path</b>, so they appear here instead of in the lists above --
    /// reporting the expression is honest where inventing a path would not be, and silence
    /// would wrongly suggest the package touches no files.
    ///
    /// This is the common case in a parameterized project, not an edge case: in this PoC,
    /// <c>LoadReferenceData</c>'s two flat-file connection managers carry no static
    /// <c>ConnectionString</c> at all (verified in the raw <c>.dtsx</c>), so every path it
    /// reads lands here. Resolving these for real needs the parameter values a given
    /// environment supplies -- an SSISDB/environment concern, i.e. exactly what the
    /// unavailable <c>enrich</c> command (plan §11 decision 4) would have provided.
    /// </summary>
    public List<string> FilePathsFromExpression { get; init; } = [];

    /// <summary>Database servers referenced by any connection manager (from the parsed connection string's Data Source). A literal here is also an environment-coupling finding -- see <c>RulesEngine</c>'s <c>hardcoded-connection-string</c> rule. Note a value here can be a design-time default that a property expression overrides at run time -- see <see cref="FilePathsFromExpression"/>.</summary>
    public List<string> Servers { get; init; } = [];
}

/// <summary>
/// One edge of the cross-package dependency graph (plan §5.2). Two kinds, and the second is
/// the one that matters: an <c>ExecutePackageTask</c> edge is an ordering dependency somebody
/// wrote down, while a <c>SharedTable</c> edge (A writes <c>dbo.X</c>, B reads <c>dbo.X</c>)
/// is one that exists only in the schedule and in nobody's documentation. On a real
/// portfolio that second set is usually the first time anyone has seen the true execution
/// order, and it determines migration sequencing.
/// </summary>
public sealed class DependencyEdgeSpec
{
    public required string FromPackage { get; init; }
    public required string ToPackage { get; init; }

    /// <summary>"ExecutePackageTask" (explicit) or "SharedTable" (implicit).</summary>
    public required string Kind { get; init; }

    /// <summary>For a <c>SharedTable</c> edge, the table both packages touch. Null for an <c>ExecutePackageTask</c> edge.</summary>
    public string? SharedObject { get; init; }

    /// <summary>Where the dependency was observed -- the Execute Package Task's refId, or a short note naming the writing/reading side for a shared-table edge.</summary>
    public required string Detail { get; init; }
}
