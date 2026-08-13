using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Finds every SQL statement in a package (plan §5.4's harvest half -- the ScriptDom parse
/// half lives in <c>Ssis.Extract.Sql</c>, kept separate so this project stays free of the
/// parser dependency). The plan names four sources: Execute SQL Task, OLE DB Source
/// <c>SqlCommand</c>, Lookup query, and OLE DB Command. All four are covered here by
/// reading the relevant pipeline component properties generically by name, so a component
/// type this build slice has no bespoke payload for still gets its SQL harvested as long as
/// it uses one of the standard property names.
/// </summary>
public static class SqlHarvester
{
    /// <summary>Property names that carry SQL on a pipeline component. <c>SqlCommand</c> covers OLE DB Source (AccessMode=SqlCommand), OLE DB Command, and Lookup's own query is <c>SqlCommand</c> too; <c>SqlCommandParam</c> is the parameterized Lookup variant.</summary>
    private static readonly string[] SqlPropertyNames = ["SqlCommand", "SqlCommandParam"];

    public readonly record struct HarvestedSql(string Location, string Sql);

    public static List<HarvestedSql> Harvest(PackageSpec package)
    {
        var results = new List<HarvestedSql>();

        foreach (var ex in PackageTree.AllExecutables(package))
        {
            if (ex.ExecuteSqlTask?.SqlStatementSource is { } sql && !string.IsNullOrWhiteSpace(sql))
            {
                // SqlStatementSourceType of FileConnection/Variable means SqlStatementSource
                // holds a connection-manager/variable NAME, not SQL text -- harvesting it
                // would feed the parser a filename. Null means the schema default
                // (DirectInput) applies, which is the only case that is real SQL.
                var sourceType = ex.ExecuteSqlTask.SqlStatementSourceType;
                if (sourceType is null || sourceType.Equals("DirectInput", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new HarvestedSql(ex.RefId, sql));
                }
            }

            if (ex.DataFlowTask is null) continue;
            foreach (var comp in ex.DataFlowTask.Pipeline.Components)
            {
                foreach (var propName in SqlPropertyNames)
                {
                    var value = comp.Properties.FirstOrDefault(p => p.Name == propName)?.Value;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        results.Add(new HarvestedSql($"{ex.RefId}/{comp.Name}.{propName}", value));
                    }
                }
            }
        }

        return results;
    }
}
