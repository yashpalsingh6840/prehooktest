using Microsoft.SqlServer.TransactSql.ScriptDom;
using Ssis.Extract.Model.Analysis;

namespace Ssis.Extract.Sql;

/// <summary>
/// Parses harvested SQL text with ScriptDom (plan §5.4) into a <see cref="SqlAnalysisSpec"/>.
/// This is the only project in the solution with a third-party parser dependency -- kept
/// separate from <c>Ssis.Extract.Dtsx</c> deliberately so the core XML readers stay
/// dependency-free (plan §3's "dependencies, deliberately few").
///
/// Parser version: <c>TSql170Parser</c> (SQL Server 2022/2025-era grammar). A newer parser
/// accepts a superset of older syntax, so this is the tolerant choice for an unknown
/// portfolio rather than a claim about the client's server version. A statement that still
/// fails to parse is reported via <see cref="SqlAnalysisSpec.ParseErrors"/>, never swallowed
/// -- on a real portfolio that usually means the Execute SQL Task targets a non-T-SQL
/// provider (Oracle/DB2/ODBC are all legal there), which is a finding in its own right.
/// </summary>
public static class SqlAnalyzer
{
    public static SqlAnalysisSpec Analyze(string packageName, string location, string sqlText, string? sqlFilePath = null)
    {
        if (string.IsNullOrWhiteSpace(sqlText))
        {
            return new SqlAnalysisSpec
            {
                PackageName = packageName,
                Location = location,
                SqlFilePath = sqlFilePath,
                ParsedSuccessfully = true,
                HasTruncate = false,
                HasDelete = false,
                HasMerge = false,
                HasDynamicSql = false,
            };
        }

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sqlText);
        var fragment = parser.Parse(reader, out var errors);

        if (errors.Count > 0)
        {
            return new SqlAnalysisSpec
            {
                PackageName = packageName,
                Location = location,
                SqlFilePath = sqlFilePath,
                ParsedSuccessfully = false,
                ParseErrors = errors.Select(e => $"line {e.Line}, col {e.Column}: {e.Message}").ToList(),
                HasTruncate = false,
                HasDelete = false,
                HasMerge = false,
                HasDynamicSql = false,
            };
        }

        var visitor = new ReferenceVisitor();
        fragment.Accept(visitor);

        return new SqlAnalysisSpec
        {
            PackageName = packageName,
            Location = location,
            SqlFilePath = sqlFilePath,
            ParsedSuccessfully = true,
            StatementTypes = visitor.StatementTypes.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            ReadsFrom = visitor.ReadsFrom.Except(visitor.WritesTo).OrderBy(s => s, StringComparer.Ordinal).ToList(),
            WritesTo = visitor.WritesTo.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            ExecutesProcedures = visitor.ExecutesProcedures.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            HasTruncate = visitor.HasTruncate,
            HasDelete = visitor.HasDelete,
            HasMerge = visitor.HasMerge,
            HasDynamicSql = visitor.HasDynamicSql,
        };
    }

    /// <summary>
    /// Walks the AST collecting referenced objects. Write targets are captured by visiting
    /// each mutating statement type explicitly and recording <i>its own</i> target, then
    /// subtracting writes from reads at the end -- because ScriptDom models an
    /// <c>INSERT INTO x</c> target as a <see cref="NamedTableReference"/> too, so a naive
    /// "every NamedTableReference is a read" would report every written table as also read.
    /// </summary>
    private sealed class ReferenceVisitor : TSqlFragmentVisitor
    {
        public readonly HashSet<string> StatementTypes = [];
        public readonly HashSet<string> ReadsFrom = [];
        public readonly HashSet<string> WritesTo = [];
        public readonly HashSet<string> ExecutesProcedures = [];
        public bool HasTruncate;
        public bool HasDelete;
        public bool HasMerge;
        public bool HasDynamicSql;

        // TSqlScript/TSqlBatch are NOT TSqlStatement subtypes (confirmed by the compiler
        // rejecting a pattern match between them), so this only ever sees real statements --
        // no container filtering needed.
        public override void Visit(TSqlStatement node)
        {
            StatementTypes.Add(node.GetType().Name);
            base.Visit(node);
        }

        public override void Visit(NamedTableReference node)
        {
            ReadsFrom.Add(Format(node.SchemaObject));
            base.Visit(node);
        }

        public override void Visit(TruncateTableStatement node)
        {
            HasTruncate = true;
            WritesTo.Add(Format(node.TableName));
            base.Visit(node);
        }

        public override void Visit(DeleteSpecification node)
        {
            HasDelete = true;
            AddTarget(node.Target);
            base.Visit(node);
        }

        public override void Visit(InsertSpecification node)
        {
            AddTarget(node.Target);
            base.Visit(node);
        }

        public override void Visit(UpdateSpecification node)
        {
            AddTarget(node.Target);
            base.Visit(node);
        }

        public override void Visit(MergeSpecification node)
        {
            HasMerge = true;
            AddTarget(node.Target);
            base.Visit(node);
        }

        public override void Visit(ExecutableProcedureReference node)
        {
            if (node.ProcedureReference?.ProcedureReference?.Name is { } name)
            {
                var formatted = Format(name);
                ExecutesProcedures.Add(formatted);
                // sp_executesql is dynamic SQL by definition -- the statement it runs
                // doesn't exist until run time, so nothing below can see its objects.
                if (formatted.EndsWith("sp_executesql", StringComparison.OrdinalIgnoreCase))
                {
                    HasDynamicSql = true;
                }
            }
            base.Visit(node);
        }

        /// <summary>An <c>EXEC('...' + @var)</c> string-concatenation form -- the other shape dynamic SQL takes, distinct from sp_executesql.</summary>
        public override void Visit(ExecutableStringList node)
        {
            HasDynamicSql = true;
            base.Visit(node);
        }

        private void AddTarget(TableReference? target)
        {
            if (target is NamedTableReference named)
            {
                WritesTo.Add(Format(named.SchemaObject));
            }
        }

        /// <summary>
        /// Renders a <see cref="SchemaObjectName"/> as written, without inventing parts the
        /// SQL text didn't state -- an unqualified <c>Employee</c> stays <c>Employee</c>
        /// rather than becoming <c>dbo.Employee</c>. Defaulting the schema would fabricate a
        /// fact (the actual default schema depends on the executing login), and the
        /// data-touch inventory is more useful reporting what the package literally says.
        /// </summary>
        private static string Format(SchemaObjectName name)
        {
            var parts = new[] { name.ServerIdentifier?.Value, name.DatabaseIdentifier?.Value, name.SchemaIdentifier?.Value, name.BaseIdentifier?.Value }
                .Where(p => !string.IsNullOrEmpty(p));
            return string.Join(".", parts);
        }
    }
}
