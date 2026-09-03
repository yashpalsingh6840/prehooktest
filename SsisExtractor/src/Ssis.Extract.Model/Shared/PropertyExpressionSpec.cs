namespace Ssis.Extract.Model.Shared;

/// <summary>
/// A <c>DTS:PropertyExpression</c> -- names the property being parameterised (e.g.
/// "ConnectionString") and the SSIS expression driving it (e.g. "@[User::SourceFilePath]").
/// Appears on connection managers, packages, and (not in this PoC) other containers.
/// </summary>
public sealed class PropertyExpressionSpec
{
    public required string PropertyName { get; init; }
    public required string Expression { get; init; }
}
