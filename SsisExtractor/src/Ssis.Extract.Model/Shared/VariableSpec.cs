namespace Ssis.Extract.Model.Shared;

/// <summary>
/// A <c>DTS:Variable</c>. Modeled at whatever scope it's declared -- slice 1 only reads
/// package-level <c>DTS:Variables</c> (both PoC packages declare theirs there, e.g.
/// LoadEmployees' <c>SourceFilePath</c>); per-container variable scoping (plan §4.4) is
/// completed in slice 2 once the executable tree is walked.
/// </summary>
public sealed class VariableSpec
{
    public required string Namespace { get; init; }
    public required string ObjectName { get; init; }
    public string? DtsId { get; init; }
    public string? CreationName { get; init; }

    /// <summary>Raw <c>DTS:DataType</c> on the VariableValue element -- see <see cref="Shared.SsisTypeCodeMaps.VariantDeclaredTypeName"/>.</summary>
    public int? DeclaredDataTypeRaw { get; init; }
    public string? DeclaredDataTypeName { get; init; }

    /// <summary>Design-time value, as text (empty string and null are distinguished).</summary>
    public string? Value { get; init; }

    public bool EvaluateAsExpression { get; init; }
    public string? Expression { get; init; }
    public bool ReadOnly { get; init; }
    public int? IncludeInDebugDump { get; init; }

    /// <summary>refId of the owning container -- "Package" for package-scoped variables.</summary>
    public required string OwningContainerRefId { get; init; }
}
