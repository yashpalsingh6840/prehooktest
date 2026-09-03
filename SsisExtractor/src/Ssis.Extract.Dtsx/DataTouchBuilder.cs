using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Builds the data-touch inventory (plan §5.3) and the cross-package dependency graph
/// (plan §5.2). Takes the already-computed <see cref="SqlAnalysisSpec"/> list as input
/// rather than parsing SQL itself, keeping this project free of the ScriptDom dependency
/// (which lives in <c>Ssis.Extract.Sql</c>) -- the caller wires the two together.
/// </summary>
public static class DataTouchBuilder
{
    public static DataTouchSpec Build(PackageSpec package, List<SqlAnalysisSpec> sqlAnalyses)
    {
        var tablesRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tablesWritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var procs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sql in sqlAnalyses)
        {
            foreach (var t in sql.ReadsFrom) tablesRead.Add(t);
            foreach (var t in sql.WritesTo) tablesWritten.Add(t);
            foreach (var p in sql.ExecutesProcedures) procs.Add(p);
        }

        foreach (var ex in PackageTree.AllExecutables(package))
        {
            if (ex.DataFlowTask is null) continue;
            foreach (var comp in ex.DataFlowTask.Pipeline.Components)
            {
                if (comp.OleDbDestination?.OpenRowset is { } target && !string.IsNullOrWhiteSpace(target))
                {
                    tablesWritten.Add(NormalizeTableName(target));
                }
            }
        }

        var (readConnections, writeConnections) = ResolveConnectionDirections(package);

        var filePathsRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filePathsWritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filePathsUnknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filePathsFromExpression = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var servers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var fileLikeCreationNames = new[] { "FLATFILE", "MULTIFLATFILE", "FILE", "MULTIFILE" };

        foreach (var cm in package.ConnectionManagers)
        {
            if (cm.Parsed?.Server is { } server && !string.IsNullOrWhiteSpace(server))
            {
                servers.Add(server);
            }

            // A whole-string ConnectionString expression owns the value outright, so any
            // static path present is only a design-time default the runtime replaces. Report
            // the expression rather than a path that won't be the one actually used.
            var connectionStringExpression = cm.PropertyExpressions.FirstOrDefault(pe => pe.PropertyName == "ConnectionString");
            if (connectionStringExpression is not null && fileLikeCreationNames.Contains(cm.CreationName, StringComparer.OrdinalIgnoreCase))
            {
                filePathsFromExpression.Add($"{cm.ObjectName} = {connectionStringExpression.Expression}");
                continue;
            }

            var path = cm.Parsed?.FilePath;
            if (string.IsNullOrWhiteSpace(path)) continue;

            if (readConnections.Contains(cm.ObjectName)) filePathsRead.Add(path);
            else if (writeConnections.Contains(cm.ObjectName)) filePathsWritten.Add(path);
            else filePathsUnknown.Add(path);
        }

        return new DataTouchSpec
        {
            PackageName = package.ObjectName,
            // A table both read and written stays in both lists -- that's a real fact about
            // the package, not a conflict to resolve (unlike SqlAnalyzer's per-statement
            // read/write disambiguation, which is about one statement's own target).
            TablesRead = Sorted(tablesRead),
            TablesWritten = Sorted(tablesWritten),
            ProceduresExecuted = Sorted(procs),
            FilePathsRead = Sorted(filePathsRead),
            FilePathsWritten = Sorted(filePathsWritten),
            FilePathsUnknownDirection = Sorted(filePathsUnknown),
            FilePathsFromExpression = Sorted(filePathsFromExpression),
            Servers = Sorted(servers),
        };
    }

    /// <summary>
    /// Connection-manager name -> whether a source or destination component uses it. A
    /// FILE/FLATFILE connection manager's own definition says nothing about direction --
    /// only the component consuming it does (plan §5.3's read-vs-written split), so this
    /// is resolved from pipeline usage rather than guessed from the connection manager.
    ///
    /// <para>Public because <see cref="ObservableEffectsBuilder"/> needs the same answer for a
    /// different question: a source CSV is an input and therefore not an effect, while a
    /// destination file is. Sharing one implementation keeps the two from drifting into
    /// disagreeing about which direction a connection manager points.</para>
    /// </summary>
    public static (HashSet<string> Read, HashSet<string> Write) ResolveConnectionDirections(PackageSpec package)
    {
        var readConnections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writeConnections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ex in PackageTree.AllExecutables(package))
        {
            if (ex.DataFlowTask is null) continue;
            foreach (var comp in ex.DataFlowTask.Pipeline.Components)
            {
                // A component with no inputs is a source; one with no non-error outputs is a
                // destination. Structural, so it works for component types this build slice
                // has no bespoke payload for -- not a componentClassID name-matching guess.
                var isSource = comp.Inputs.Count == 0;
                var isDestination = comp.Outputs.Count(o => o.IsErrorOut != true) == 0;

                foreach (var conn in comp.Connections.Where(c => c.ConnectionManagerName is not null))
                {
                    if (isSource) readConnections.Add(conn.ConnectionManagerName!);
                    else if (isDestination) writeConnections.Add(conn.ConnectionManagerName!);
                }
            }
        }

        return (readConnections, writeConnections);
    }

    /// <summary>Strips SSIS/T-SQL bracket quoting so <c>[dbo].[Employee]</c> (how an OLE DB Destination writes its OpenRowset) and <c>dbo.Employee</c> (how SQL text usually writes it) become the same string -- without this, a package that truncates a table via SQL and writes it via a destination looks like it touches two different tables, and the §5.2 shared-table dependency edges silently miss.</summary>
    private static string NormalizeTableName(string raw) => raw.Replace("[", "").Replace("]", "");

    private static List<string> Sorted(HashSet<string> set) => set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
}
