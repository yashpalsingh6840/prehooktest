namespace Ssis.Extract.Model.Analysis;

/// <summary>
/// Result of parsing one harvested SQL statement with ScriptDom (plan §5.4). Turns SQL text
/// from an opaque blob into structured dependency data feeding the data-touch inventory
/// (§5.3) and the cross-package dependency graph (§5.2).
/// </summary>
public sealed class SqlAnalysisSpec
{
    /// <summary>Where this SQL came from -- an Execute SQL Task's refId, or a
    /// "&lt;DFT refId&gt;/&lt;component name&gt;.&lt;property&gt;" path for pipeline SQL
    /// (OLE DB Source/Destination <c>SqlCommand</c>, Lookup query, OLE DB Command).</summary>
    public required string Location { get; init; }

    public required string PackageName { get; init; }

    /// <summary>Relative path of the <c>.sql</c> file this statement was written to under the report's <c>sql/</c> directory (plan §6.1: "every SQL statement, individually addressable"). Null when the analysis was run without writing files.</summary>
    public string? SqlFilePath { get; init; }

    /// <summary>
    /// False when ScriptDom couldn't parse the text at all -- <see cref="ParseErrors"/>
    /// carries why. A parse failure is reported, never swallowed: on a client portfolio it
    /// usually means dialect drift (a newer/older T-SQL feature, or non-T-SQL entirely --
    /// an Execute SQL Task can target Oracle/DB2/ODBC just as easily), which is itself a
    /// finding, not a tool bug. Everything below is empty/false when this is false.
    /// </summary>
    public required bool ParsedSuccessfully { get; init; }

    public List<string> ParseErrors { get; init; } = [];

    /// <summary>Distinct ScriptDom statement type names, e.g. "TruncateTableStatement", "InsertStatement" -- the raw AST node names rather than a hand-maintained friendly-name mapping, so an unanticipated statement type shows up honestly rather than as "Other".</summary>
    public List<string> StatementTypes { get; init; } = [];

    /// <summary>Tables/views read from (FROM/JOIN/USING). Normalized to <c>schema.name</c> where a schema is written, bare <c>name</c> where it isn't -- deliberately not defaulted to <c>dbo</c>, since that would fabricate a fact the SQL text doesn't state.</summary>
    public List<string> ReadsFrom { get; init; } = [];

    /// <summary>Tables written to (INSERT/UPDATE/DELETE/MERGE target/TRUNCATE), same normalization as <see cref="ReadsFrom"/>.</summary>
    public List<string> WritesTo { get; init; } = [];

    /// <summary>Stored procedures invoked by EXEC.</summary>
    public List<string> ExecutesProcedures { get; init; } = [];

    public required bool HasTruncate { get; init; }
    public required bool HasDelete { get; init; }
    public required bool HasMerge { get; init; }

    /// <summary>An <c>EXEC(@sql)</c>/<c>sp_executesql</c>-style construct -- the referenced-object lists above are necessarily incomplete when this is true, because the real statement doesn't exist until run time.</summary>
    public required bool HasDynamicSql { get; init; }
}
