using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits a genuinely multi-independent-source <c>Microsoft.Merge</c>/<c>Microsoft.UnionAll</c>'s
/// own OUTPUT row shape plus ONE shared reader for it -- gap-audit Phase 3.6 (2026-09-02).
/// Deliberately NOT built on <see cref="Ssis.Extract.Dtsx.PipelineResolver"/> (see
/// <see cref="EmitRowType"/>'s own doc comment), same reason <see cref="MergeJoinEmitter"/>/
/// <see cref="AggregateRowEmitter"/> already resolve their own row types straight off raw output
/// columns.
///
/// <b>ONE reader class is shared by every side</b>, keyed on the union's OWN output column names.
/// That is safe only because <c>PackagePlanner.PlanUnion</c> now guarantees each side actually
/// EXPOSES those names -- either because they already match, or because the side is an OLE DB
/// Source in OpenRowset mode whose generated SELECT is aliased to them.
///
/// <para><b>An earlier version of this comment claimed "real SSIS enforces matching schemas
/// across every Merge/UnionAll input at design time". That is false</b>, measured 2026-09-02: a
/// Union All whose output column is named <c>UnifiedName</c> accepted input 1 mapping
/// <c>CustName -&gt; UnifiedName</c> and input 2 mapping <c>ClientName -&gt; UnifiedName</c> --
/// three different names -- and validated VS_ISVALID. The real mapping is per-input and explicit,
/// in each input column's own <c>OutputColumnLineageID</c>, which nothing in this tool read at
/// all until the gap audit. Applying this reader blindly to such a side threw on
/// <c>GetOrdinal</c> at best; a SWAPPED mapping was worse, silently transposing that side's
/// values because every name still resolved.</para>
/// </summary>
public static class UnionEmitter
{
    /// <summary>Resolves straight off <paramref name="unionComponent"/>'s own raw output columns
    /// -- a Merge/UnionAll's own output has NO external metadata columns at all (confirmed real
    /// the same way MergeJoinEmitter/AggregateRowEmitter already established for their own
    /// components: it is a pure pipeline transform with no design-time database/file schema of
    /// its own), so there is nothing for <see cref="Ssis.Extract.Dtsx.PipelineResolver"/> to join
    /// against.</summary>
    public static EmitResult EmitRowType(string ns, string rowClassName, PipelineComponentSpec unionComponent)
    {
        var output = unionComponent.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (output is null)
        {
            return new EmitResult([], [new GenerationGap(rowClassName, $"'{unionComponent.Name}' has no main (non-error) output")]);
        }

        // EmitReader independently reproduces this exact mapping (same output.Columns list, same
        // order) -- see PackageGenerator.MakeColumnIdentifierResolver's own doc comment.
        var identifierOf = PackageGenerator.MakeColumnIdentifierResolver();

        var gaps = new List<GenerationGap>();
        var propertyLines = new List<string>();
        foreach (var column in output.Columns)
        {
            var type = column.DataType is null ? null : SsisPipelineTypeMap.Resolve(column.DataType);
            if (type is null)
            {
                gaps.Add(new GenerationGap($"{rowClassName}.{column.Name}",
                    $"unmapped pipeline data type '{column.DataType}' -- add it to SsisPipelineTypeMap before this column can be generated"));
                continue;
            }

            if (propertyLines.Count > 0) propertyLines.Add("");
            var initializer = type.ClrTypeName == "string" ? " = \"\";" : "";
            propertyLines.Add($"    public {type.ClrTypeName} {identifierOf(column.Name)} {{ get; set; }}{initializer}");
        }

        if (propertyLines.Count == 0)
        {
            if (gaps.Count == 0) gaps.Add(new GenerationGap(rowClassName, "no columns could be generated"));
            return new EmitResult([], gaps);
        }

        var lines = new List<string> { $"namespace {ns};", "", $"public sealed class {rowClassName}", "{" };
        lines.AddRange(propertyLines);
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"Sql/{rowClassName}.cs", Rendering.JoinLines(lines))], gaps);
    }

    /// <summary>Emits <c>Sql/{rowClassName}Reader.cs</c>, reading <paramref name="unionComponent"/>'s
    /// own output columns BY NAME off a <c>DbDataReader</c> -- the SAME class name/file
    /// <see cref="SqlRowReaderEmitter"/> would produce for an ordinary single-source flow, so
    /// every side's own <c>SqlFlowSource</c> registration (built against the shared row type) can
    /// reference it identically, with no per-side reader to keep in sync.</summary>
    public static EmitResult EmitReader(string ns, string rowClassName, PipelineComponentSpec unionComponent)
    {
        var output = unionComponent.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (output is null)
        {
            return new EmitResult([], [new GenerationGap($"{rowClassName}Reader", $"'{unionComponent.Name}' has no main (non-error) output")]);
        }

        // Independently reproduces EmitRowType's own identifier mapping -- see
        // PackageGenerator.MakeColumnIdentifierResolver's own doc comment.
        var identifierOf = PackageGenerator.MakeColumnIdentifierResolver();

        var assignments = new List<string>();
        foreach (var column in output.Columns)
        {
            var type = column.DataType is null ? null : SsisPipelineTypeMap.Resolve(column.DataType);
            // Unmapped-type columns are already reported by EmitRowType against the same row
            // type -- don't double-report the same fact from a second emitter.
            if (type is null) continue;

            // The union's own output column name is what SqlRowReaderEmitter-style OLE DB Source
            // SELECT aliasing guarantees is really present in the result set -- a LITERAL lookup,
            // stays raw. The assignment target is the sanitized identifier.
            var ordinalExpr = $"reader.GetOrdinal(\"{column.Name}\")";
            assignments.Add($"        {identifierOf(column.Name)} = reader.GetFieldValue<{type.ClrTypeName}>({ordinalExpr}),");
        }

        if (assignments.Count == 0)
        {
            return new EmitResult([], [new GenerationGap($"{rowClassName}Reader", "no columns to read -- see this row type's own EmitRowType gaps")]);
        }

        var lines = new List<string>
        {
            "using System.Data.Common;",
            "",
            $"namespace {ns};",
            "",
            $"public static class {rowClassName}Reader",
            "{",
            $"    public static {rowClassName} Read(DbDataReader reader) => new()",
            "    {",
        };
        lines.AddRange(assignments);
        lines.Add("    };");
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"Sql/{rowClassName}Reader.cs", Rendering.JoinLines(lines))], []);
    }
}
