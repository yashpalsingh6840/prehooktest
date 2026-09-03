using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Dtsx;

/// <summary>One pipeline column joined to the external (database/file) column it maps to, with every facet recovered.</summary>
/// <param name="PipelineColumnName">The column's name inside the buffer.</param>
/// <param name="ExternalColumnName">The destination column name -- what a generated entity property maps to.</param>
/// <param name="ExternalDataType">The external column's raw textual type, e.g. <c>"wstr"</c>.</param>
/// <param name="Length">From the EXTERNAL column, not the buffer column -- see <see cref="PipelineResolver"/>.</param>
/// <param name="Precision">From the external column.</param>
/// <param name="Scale">From the external column.</param>
/// <param name="CodePage">From the external column.</param>
/// <param name="Type">Resolved CLR/EF mapping, or null when <see cref="ExternalDataType"/> is not in <see cref="SsisPipelineTypeMap"/>. Null means "report this column", never "pick a default".</param>
/// <param name="PipelineColumnRefId">Stable identity -- use this, never the name, when deriving generated identifiers.</param>
public sealed record ResolvedColumn(
    string PipelineColumnName,
    string ExternalColumnName,
    string? ExternalDataType,
    int? Length,
    int? Precision,
    int? Scale,
    int? CodePage,
    SsisPipelineType? Type,
    string PipelineColumnRefId);

/// <summary>A column that could not be joined to an external column at all. Surfaced, never dropped.</summary>
public sealed record UnresolvedColumn(string ColumnName, string ColumnRefId, string Reason);

/// <summary>Everything one input/output contributed, successes and failures kept side by side.</summary>
public sealed record ResolvedColumnSet(string OwnerRefId, List<ResolvedColumn> Columns, List<UnresolvedColumn> Unresolved);

/// <summary>
/// Rejoins pipeline columns to their external metadata columns, recovering the facets that
/// <see cref="PipelineColumnMappingSpec"/> throws away.
///
/// <para><b>Why this exists.</b> <c>PipelineReader.BuildColumnMappings</c> flattens the join
/// down to three fields -- component column name, external column name, external data type --
/// and discards <c>Length</c>/<c>Precision</c>/<c>Scale</c>/<c>CodePage</c>. Those are exactly
/// the values an EF Core model needs (<c>HasMaxLength(20)</c>, <c>decimal(18,2)</c>,
/// <c>datetime2(3)</c>), so any consumer that needs them has to redo the join itself. Doing
/// that once here keeps every emitter from re-deriving it slightly differently.</para>
///
/// <para><b>Facets come from the EXTERNAL column, deliberately.</b> The external metadata column
/// is the design-time contract with the database, so it is what describes the target schema; the
/// buffer column describes the in-flight value. They usually agree, and where they don't it is
/// the external side that a destination table must match. Verified against this repo's own
/// fixtures: <c>EmployeeKey</c> external is <c>wstr</c>/Length 20, <c>Salary</c> is
/// <c>numeric</c>/18/2, <c>LoadedAtUtc</c> is <c>dbTimeStamp2</c>/Scale 3 -- which is exactly
/// what the hand-written rewrite's DbContext declares.</para>
///
/// <para><b>Unmapped columns are reported, not skipped.</b> <c>BuildColumnMappings</c> hits
/// <c>continue</c> on a null or dangling <c>ExternalMetadataColumnId</c>, so the mapping list can
/// be shorter than the column list with no signal at all. Here every such column lands in
/// <see cref="ResolvedColumnSet.Unresolved"/> with a reason -- the same "a gap must be visible"
/// rule the observable-effects and load-failure work already follow.</para>
/// </summary>
public static class PipelineResolver
{
    /// <summary>Resolves the single input of a destination-shaped component. Returns an empty set (with a reason) when the component has no input or more than one.</summary>
    public static ResolvedColumnSet ResolveDestinationInput(PipelineComponentSpec component)
    {
        if (component.Inputs.Count != 1)
        {
            return new ResolvedColumnSet(component.RefId, [], [
                new UnresolvedColumn(component.Name, component.RefId,
                    $"expected exactly one input on '{component.ComponentClassId}', found {component.Inputs.Count}")
            ]);
        }
        return Resolve(component.Inputs[0]);
    }

    public static ResolvedColumnSet Resolve(PipelineInputSpec input)
    {
        var external = BuildExternalIndex(input.ExternalMetadataColumns);
        var columns = new List<ResolvedColumn>();
        var unresolved = new List<UnresolvedColumn>();

        foreach (var col in input.Columns)
        {
            Add(columns, unresolved, col.RefId, col.CachedName, col.ExternalMetadataColumnId, external);
        }

        return new ResolvedColumnSet(input.RefId, columns, unresolved);
    }

    public static ResolvedColumnSet Resolve(PipelineOutputSpec output)
    {
        var external = BuildExternalIndex(output.ExternalMetadataColumns);
        var columns = new List<ResolvedColumn>();
        var unresolved = new List<UnresolvedColumn>();

        foreach (var col in output.Columns)
        {
            Add(columns, unresolved, col.RefId, col.Name, col.ExternalMetadataColumnId, external);
        }

        return new ResolvedColumnSet(output.RefId, columns, unresolved);
    }

    private static Dictionary<string, PipelineExternalMetadataColumnSpec> BuildExternalIndex(
        List<PipelineExternalMetadataColumnSpec> externalColumns)
    {
        // Indexed by RefId with last-wins rather than Add(), because a duplicate RefId would
        // otherwise throw and take down the whole run over a malformed package -- the same
        // failure shape that duplicate component names caused in ssisx conformance.
        var index = new Dictionary<string, PipelineExternalMetadataColumnSpec>(StringComparer.Ordinal);
        foreach (var ext in externalColumns) index[ext.RefId] = ext;
        return index;
    }

    private static void Add(
        List<ResolvedColumn> columns,
        List<UnresolvedColumn> unresolved,
        string columnRefId,
        string columnName,
        string? externalMetadataColumnId,
        Dictionary<string, PipelineExternalMetadataColumnSpec> external)
    {
        if (externalMetadataColumnId is null)
        {
            unresolved.Add(new UnresolvedColumn(columnName, columnRefId,
                "column carries no ExternalMetadataColumnId -- it is not mapped to an external (database/file) column"));
            return;
        }

        if (!external.TryGetValue(externalMetadataColumnId, out var ext))
        {
            unresolved.Add(new UnresolvedColumn(columnName, columnRefId,
                $"ExternalMetadataColumnId '{externalMetadataColumnId}' does not match any external metadata column on this input/output"));
            return;
        }

        columns.Add(new ResolvedColumn(
            PipelineColumnName: columnName,
            ExternalColumnName: ext.Name,
            ExternalDataType: ext.DataType,
            Length: ext.Length,
            Precision: ext.Precision,
            Scale: ext.Scale,
            CodePage: ext.CodePage,
            Type: SsisPipelineTypeMap.Resolve(ext.DataType),
            PipelineColumnRefId: columnRefId));
    }
}
