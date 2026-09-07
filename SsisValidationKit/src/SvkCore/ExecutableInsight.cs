using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;

namespace Svk.Core;

/// <summary>
/// Summarizes what one control-flow executable actually DOES -- unlike a pipeline component
/// (see <see cref="ComponentInsight"/>), an executable's own <c>Description</c> IS genuine,
/// author-written insight in real packages checked so far (e.g. "Writes to SSISDemoCache - the
/// second database in this package", "Deliberately disabled - mirrors the disabled UTF-8
/// converter..."), so it's surfaced verbatim here rather than discarded as boilerplate. Combined
/// with the task's own bespoke payload (the real SQL text, file paths, script language) so a
/// reviewer reading just the walkthrough doesn't have to open the `.dtsx` to know what a task does.
/// </summary>
public static class ExecutableInsight
{
    /// <summary>One line per fact worth showing; empty when the executable has nothing beyond its
    /// own heading (a plain container, or a task type with no bespoke payload modeled).</summary>
    public static List<string> Describe(ExecutableSpec ex)
    {
        var lines = new List<string>();

        if (!string.IsNullOrEmpty(ex.Description))
        {
            lines.Add($"**Note:** {ex.Description}");
        }

        if (ex.ExecuteSqlTask is { } sql)
        {
            var conn = string.IsNullOrEmpty(sql.ConnectionName) ? "" : $" (on `{sql.ConnectionName}`)";
            lines.Add($"**SQL**{conn}: `{Truncate(sql.SqlStatementSource)}`");
        }

        if (ex.FileSystemTask is { } fs)
        {
            var op = fs.OperationRaw ?? "CopyFile";
            var source = fs.SourceConnectionName ?? fs.SourcePathRaw ?? "?";
            var dest = fs.DestinationConnectionName ?? fs.DestinationPathRaw ?? "?";
            lines.Add($"**File System:** {op} `{source}` -> `{dest}`" + (fs.OverwriteDestination == true ? " (overwrites)" : ""));
        }

        if (ex.ScriptTask is { } script)
        {
            var detail = $"**Script** ({script.Language})";
            var reads = script.ReadOnlyVariables.Count > 0 ? $"reads {string.Join(", ", script.ReadOnlyVariables)}" : null;
            var writes = script.ReadWriteVariables.Count > 0 ? $"writes {string.Join(", ", script.ReadWriteVariables)}" : null;
            var vars = string.Join("; ", new[] { reads, writes }.Where(v => v is not null));
            lines.Add(vars.Length > 0 ? $"{detail}: {vars}" : detail);
        }

        return lines;
    }

    /// <summary>Same collapsing/truncation rule as <see cref="ComponentInsight"/>'s own helper --
    /// real Execute SQL Task statements in this portfolio are routinely multi-line
    /// (`BEGIN TRANSACTION`/`TRY`/`CATCH` blocks), and a literal newline would break the
    /// Markdown blockquote this renders into.</summary>
    private static string Truncate(string? s, int max = 100)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var oneLine = s.Replace("\r", " ").Replace("\n", " ");
        while (oneLine.Contains("  ")) oneLine = oneLine.Replace("  ", " ");
        oneLine = oneLine.Trim();
        return oneLine.Length > max ? oneLine[..max] + "…" : oneLine;
    }
}
