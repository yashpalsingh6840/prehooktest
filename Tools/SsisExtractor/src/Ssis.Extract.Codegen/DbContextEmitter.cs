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
/// this emitter. A missing/low-confidence/composite candidate skips HasKey for that table and
/// is not reported as a gap: PrimaryKeyInference already reports its own "Unknown" candidates
/// through its own output, and letting EF's OWN implicit "Id"/"{Type}Id" key-discovery
/// convention run is usually enough. When NEITHER exists at all, though, EF's model validation
/// throws at first use ("requires a primary key to be defined") -- confirmed real 2026-08-28 by
/// actually running a generated package whose destination table has no such column at all (an
/// Excel Source's own worksheet-driven columns, none integer-typed); <see cref="EmitOneTable"/>
/// emits an explicit <c>HasNoKey()</c> in exactly that case, safe because
/// <c>Etl.Core.Data.SqlBulkSink&lt;TEntity&gt;</c> never uses EF's change tracking at all.
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
            // one regardless of whether anything maps to a table, so the generated Program.cs/
            // ExecuteSqlStep/FlatFileBulkSink all still need SOMETHING to resolve. Every table that was PASSED
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

        // Independently reproduces EntityEmitter's own identifier mapping -- see
        // PackageGenerator.MakeColumnIdentifierResolver's own doc comment. A real external column
        // name (e.g. "WWI Stock Item ID") whose sanitized identifier differs from the raw name
        // gets an explicit .HasColumnName(...) below, in EmitPropertyConfig -- without it, EF's
        // own default convention would map the SANITIZED property to a column of that same
        // sanitized (nonexistent) name, silently targeting the wrong column at run time.
        var identifierOf = PackageGenerator.MakeColumnIdentifierResolver();

        var lines = new List<string>
        {
            $"        modelBuilder.Entity<{table.EntityName}>(entity =>",
            "        {",
            $"            entity.ToTable(\"{tableName}\", \"{schema}\");",
        };

        var keyColumn = table.PrimaryKey is { Confidence: "NamingConvention", Columns.Count: 1 } pk ? pk.Columns[0] : null;
        if (keyColumn is not null)
        {
            var keyIdentifier = identifierOf(keyColumn);
            var keyColumnNameConfig = keyIdentifier != keyColumn ? $".HasColumnName(\"{keyColumn}\")" : "";
            lines.Add($"            entity.HasKey(e => e.{keyIdentifier});");
            lines.Add($"            entity.Property(e => e.{keyIdentifier}).ValueGeneratedNever(){keyColumnNameConfig};");
        }
        else if (!resolved.Columns.Any(c => HasEfConventionKeyShape(c.PipelineColumnName, table.EntityName)))
        {
            // Confirmed real, not assumed, by actually RUNNING a generated package (2026-08-28):
            // this doc comment's own earlier claim -- "EF works fine with a keyless-by-convention
            // entity here" -- was only true by accident, because every real table examined before
            // this one happened to already carry a column matching EF's OWN bare "Id"/"{Type}Id"
            // key-discovery convention, even when PrimaryKeyInference's own stricter integer-typed
            // "<table>ID"/"ID" naming rule didn't confidently confirm it. The first table with
            // NEITHER (RBC_Demo_ETL/an Excel Source's own worksheet-driven columns, none
            // integer-typed) throws InvalidOperationException ("requires a primary key to be
            // defined") from EF's own model validation at FIRST USE -- invisible to `dotnet build`,
            // breaking the WHOLE package, not just this table, only at runtime. HasNoKey() is
            // exactly EF's own suggested fix in that exception's own message, and is safe
            // unconditionally here: SqlBulkSink<TEntity> (Etl.Core.Data.SqlBulkSink.cs) never uses
            // EF's change tracking at all -- only EntityTableMap's own schema metadata read, which
            // a keyless entity type still provides.
            lines.Add("            entity.HasNoKey();");
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

            var propertyIdentifier = identifierOf(column.ExternalColumnName);
            // Whenever sanitizing changed the identifier, EF's own default convention (map a
            // property to a column of the SAME name) would otherwise target a column that does
            // not exist -- an explicit .HasColumnName(...) restores the real mapping. Emitted
            // regardless of whether this column also has a facet, since a renamed column with no
            // facet would otherwise get NO fluent config line at all.
            var columnNameConfig = propertyIdentifier != column.ExternalColumnName
                ? $".HasColumnName(\"{column.ExternalColumnName}\")"
                : "";
            if (facet is null && columnNameConfig.Length == 0) continue;

            lines.Add($"            entity.Property(e => e.{propertyIdentifier}){columnNameConfig}{facet};");
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

    /// <summary>EF Core's OWN default key-discovery convention (independent of anything
    /// PrimaryKeyInference reports): a property named exactly "Id" or "{EntityTypeName}Id"
    /// (case-insensitive, per EF's own documented convention), regardless of CLR type. Used only
    /// to decide whether letting EF's implicit convention run (by emitting neither HasKey nor
    /// HasNoKey) is actually safe -- see this method's own call site.</summary>
    private static bool HasEfConventionKeyShape(string pipelineColumnName, string entityName) =>
        string.Equals(pipelineColumnName, "Id", StringComparison.OrdinalIgnoreCase)
        || string.Equals(pipelineColumnName, entityName + "Id", StringComparison.OrdinalIgnoreCase);

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
