using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits one DbContext covering one or more destination tables, e.g.
/// LoadReferenceData.Model.ReferenceDataDbContext (Department + Designation in one context,
/// matching how the hand-written rewrite grouped them -- one context per package, not one per
/// table). <see cref="OpenRowset"/> gives ToTable's schema/table; per-column facets come from
/// <see cref="SsisPipelineTypeMap"/> the same way <see cref="EntityEmitter"/> reads it.
///
/// Deliberately emits no <c>.IsRequired()</c> anywhere: nullability isn't in a .dtsx at all
/// (PrimaryKeyCandidateSpec's own doc comment already establishes this), and EF's convention
/// already treats a non-nullable CLR property as required -- so the emitted model is
/// behaviourally identical to the hand-written one without claiming information the package
/// never carried.
///
/// HasKey/ValueGeneratedNever are gated on <see cref="PrimaryKeyCandidateSpec.Confidence"/>
/// being exactly "NamingConvention" and a single-column candidate -- the plan's own gate for
/// this emitter. A missing/low-confidence/composite candidate just skips HasKey for that
/// table and is not reported as a gap: EF works fine with a keyless-by-convention entity here
/// (SqlBulkSink never uses EF's own SaveChanges path), and PrimaryKeyInference already reports
/// its own "Unknown" candidates through its own output.
/// </summary>
public static class DbContextEmitter
{
    public sealed record TableSpec(string EntityName, PipelineComponentSpec DestinationComponent, PrimaryKeyCandidateSpec? PrimaryKey);

    public static EmitResult Emit(string ns, string contextName, IReadOnlyList<TableSpec> tables)
    {
        var gaps = new List<GenerationGap>();
        var dbSetLines = new List<string>();
        var modelBlocks = new List<List<string>>();

        foreach (var table in tables)
        {
            var block = EmitOneTable(table, gaps);
            if (block is null) continue;

            dbSetLines.Add($"    public DbSet<{table.EntityName}> {Pluralize(table.EntityName)} => Set<{table.EntityName}>();");
            modelBlocks.Add(block);
        }

        if (dbSetLines.Count == 0)
        {
            // Two different situations produce zero DbSets, and only one is an actual failure:
            // an empty TABLES LIST (a package whose only destinations are Flat File
            // Destinations, e.g. RBC_Demo_ETL's own Package_Exports.dtsx once its one ADO NET
            // destination hits its own separate, pre-existing "direct-copy" gap) still needs A
            // DbContext -- IUnitOfWork's whole-package transaction (UnitOfWork.cs) is backed by
            // one regardless of whether anything maps to a table, so PackageRunner/ExecuteSqlStep/
            // FlatFileBulkSink all still need SOMETHING to resolve. Every table that was PASSED
            // IN but individually failed EmitOneTable (a real per-table generation problem) still
            // fails the whole context, unchanged.
            if (tables.Count > 0)
            {
                if (gaps.Count == 0) gaps.Add(new GenerationGap(contextName, "no tables could be generated"));
                return new EmitResult([], gaps);
            }
        }

        var lines = new List<string>
        {
            "using Microsoft.EntityFrameworkCore;",
            "",
            $"namespace {ns};",
            "",
            $"public sealed class {contextName}(DbContextOptions<{contextName}> options) : DbContext(options)",
            "{",
        };
        lines.AddRange(dbSetLines);
        lines.Add("");
        lines.Add("    protected override void OnModelCreating(ModelBuilder modelBuilder)");
        lines.Add("    {");
        for (var i = 0; i < modelBlocks.Count; i++)
        {
            lines.AddRange(modelBlocks[i]);
            if (i < modelBlocks.Count - 1) lines.Add("");
        }
        lines.Add("    }");
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"Model/{contextName}.cs", Rendering.JoinLines(lines))], gaps);
    }

    private static List<string>? EmitOneTable(TableSpec table, List<GenerationGap> gaps)
    {
        var openRowset = DestinationInfo.TableName(table.DestinationComponent);
        if (string.IsNullOrEmpty(openRowset))
        {
            gaps.Add(new GenerationGap(table.EntityName, "destination has no OpenRowset/TableOrViewName -- SQL-command destinations are not supported yet"));
            return null;
        }

        var (schema, tableName) = ParseOpenRowset(openRowset);
        var resolved = PipelineResolver.ResolveDestinationInput(table.DestinationComponent);
        foreach (var unresolved in resolved.Unresolved)
            gaps.Add(new GenerationGap($"{table.EntityName}.{unresolved.ColumnName}", unresolved.Reason));

        var lines = new List<string>
        {
            $"        modelBuilder.Entity<{table.EntityName}>(entity =>",
            "        {",
            $"            entity.ToTable(\"{tableName}\", \"{schema}\");",
        };

        var keyColumn = table.PrimaryKey is { Confidence: "NamingConvention", Columns.Count: 1 } pk ? pk.Columns[0] : null;
        if (keyColumn is not null)
        {
            lines.Add($"            entity.HasKey(e => e.{keyColumn});");
            lines.Add($"            entity.Property(e => e.{keyColumn}).ValueGeneratedNever();");
        }

        foreach (var column in resolved.Columns)
        {
            if (column.Type is null)
            {
                gaps.Add(new GenerationGap($"{table.EntityName}.{column.ExternalColumnName}",
                    $"unmapped pipeline data type '{column.ExternalDataType}'"));
                continue;
            }

            var facet = column.Type.Facet switch
            {
                SsisFacetKind.MaxLength when column.Length is int len => $".HasMaxLength({len})",
                SsisFacetKind.ColumnType => RenderColumnTypeFacet(table.EntityName, column, gaps),
                _ => null,
            };
            if (facet is null) continue;

            lines.Add($"            entity.Property(e => e.{column.ExternalColumnName}){facet};");
        }

        lines.Add("        });");
        return lines;
    }

    private static string? RenderColumnTypeFacet(string entityName, ResolvedColumn column, List<GenerationGap> gaps)
    {
        var rendered = SsisPipelineTypeMap.RenderColumnType(column.Type!, column.Length, column.Precision, column.Scale);
        if (rendered is null)
        {
            gaps.Add(new GenerationGap($"{entityName}.{column.ExternalColumnName}",
                $"'{column.ExternalDataType}' needs a length/precision/scale facet the external column doesn't carry"));
            return null;
        }
        return $".HasColumnType(\"{rendered}\")";
    }

    /// <summary>"[dbo].[Employee]" -> ("dbo", "Employee"). Both real PoC packages always emit
    /// the bracketed two-part form for a table/view OpenRowset; an unmatched shape defaults
    /// the schema to "dbo" rather than failing, since it's unevidenced, not invalid.</summary>
    private static (string Schema, string Table) ParseOpenRowset(string openRowset)
    {
        var parts = openRowset.Split("].[", StringSplitOptions.None);
        return parts.Length == 2
            ? (parts[0].TrimStart('['), parts[1].TrimEnd(']'))
            : ("dbo", openRowset.Trim('[', ']'));
    }

    /// <summary>Deliberately minimal -- covers every entity name this PoC has (Employee,
    /// Department, Designation all just take "s"), not a general English pluralizer. A wrong
    /// DbSet name is cosmetic (EF doesn't require correct pluralization); extend the two rules
    /// below only once a real client entity name needs one of them.</summary>
    private static string Pluralize(string entityName) => entityName switch
    {
        _ when entityName.EndsWith('y') && entityName.Length > 1 && !"aeiou".Contains(entityName[^2]) =>
            entityName[..^1] + "ies",
        _ when entityName.EndsWith('s') || entityName.EndsWith('x') || entityName.EndsWith("ch", StringComparison.Ordinal) || entityName.EndsWith("sh", StringComparison.Ordinal) =>
            entityName + "es",
        _ => entityName + "s",
    };
}
