namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits the fixed row type a Flat File Source's own error output always produces -- exactly 3
/// columns, regardless of how many real columns the main output declares (confirmed via a real
/// object-model probe, <c>Ssis.Extract.FixtureBuilder</c>'s "flat-file-error-probe" mode, read
/// back as "Flat File Source Error Output Column (DT_TEXT), ErrorCode (DT_I4), ErrorColumn
/// (DT_I4)"): the two system diagnostic columns plus a single raw-text column carrying the whole
/// failed row's own source line.
///
/// <para>The raw-text column's own C# property is named from its REAL pipeline column name
/// (sanitized), never a fixed name like "RawRow" -- so <c>TransformEmitter</c>'s existing generic
/// lineage-based passthrough resolution (<c>PipelineResolver.ResolveDestinationInput</c> +
/// <c>LineageBuilder</c>) wires it up with ZERO new bridging code, the identical mechanism every
/// other row type's own properties already use. <c>PackageClassEmitter</c> constructs this type
/// positionally from <c>Etl.Core.Csv.CsvErrorRow</c> (whose own property IS literally named
/// "RawRow" -- an internal-only Etl.Core name, never seen by TransformEmitter, since a positional
/// record constructor call cares about argument POSITION/TYPE, never property names matching
/// between the two types).</para>
/// </summary>
public static class FlatFileErrorRowEmitter
{
    public static EmitResult Emit(string ns, string entityName, string rawColumnName)
    {
        var rawColumnIdentifier = PackageGenerator.SanitizeIdentifier(rawColumnName);
        var lines = new List<string>
        {
            $"namespace {ns}.Csv;",
            "",
            "/// <summary>Fixed shape of a Flat File Source's own error output -- see",
            "/// Etl.Core.Csv.CsvErrorRow's own doc comment for how these three values are",
            "/// populated and measured against a real dtexec run.</summary>",
            $"public sealed record {entityName}ErrorRow(string {rawColumnIdentifier}, int ErrorCode, int ErrorColumn);",
        };
        return new EmitResult([new GeneratedFile($"Csv/{entityName}ErrorRow.cs", Rendering.JoinLines(lines))], []);
    }
}
