namespace Ssis.Extract.Model.Analysis;

/// <summary>
/// One SSIS expression, from anywhere in a package (plan §5.5). Two uses the plan names
/// explicitly: (a) each row is a table-driven unit test case for the rewrite -- which is
/// why <see cref="FriendlyExpression"/> and the referenced columns/variables are kept
/// alongside the raw form; (b) tokenized across the portfolio, they inventory which SSIS
/// functions the codebase actually uses, so a replacement engine's required surface area is
/// a measured number rather than a guess.
/// </summary>
public sealed class HarvestedExpressionSpec
{
    public required string PackageName { get; init; }

    /// <summary>"PropertyExpression" | "ConnectionManagerExpression" | "VariableExpression" | "PrecedenceConstraint" | "DerivedColumn" -- where in the package's structure this expression lives, i.e. what evaluates it.</summary>
    public required string Kind { get; init; }

    /// <summary>The owning object's refId/name -- an executable refId, a connection manager name, a "&lt;DFT&gt;/&lt;component&gt;" path, etc.</summary>
    public required string Location { get; init; }

    /// <summary>Which property the expression drives (e.g. "ConnectionString"), or the produced column name for a Derived Column. Null where the expression isn't attached to a named property (e.g. a precedence constraint's own condition).</summary>
    public string? TargetProperty { get; init; }

    /// <summary>The expression as stored. For a Derived Column this is the <c>#{lineageId}</c> form.</summary>
    public required string Expression { get; init; }

    /// <summary>The human-readable form where the source XML carries one separately (Derived Column's <c>FriendlyExpression</c>); null elsewhere, where <see cref="Expression"/> is already the readable form.</summary>
    public string? FriendlyExpression { get; init; }

    /// <summary>SSIS variables/parameters referenced as <c>@[Namespace::Name]</c>, distinct, in first-appearance order.</summary>
    public List<string> ReferencedVariables { get; init; } = [];

    /// <summary>Upstream pipeline columns referenced as <c>#{lineageId}</c> (Derived Column expressions only), distinct, in first-appearance order.</summary>
    public List<string> ReferencedColumns { get; init; } = [];

    /// <summary>
    /// SSIS expression-language functions used, uppercased and distinct. Detected by
    /// scanning for <c>NAME(</c> patterns, with SSIS's own <c>[FUNCTION]</c> bracket form
    /// (which is how a Derived Column's stored raw expression writes them -- e.g.
    /// <c>[UPPER]([SUBSTRING](...))</c>) handled too. Cast operators (<c>(DT_WSTR,50)</c>)
    /// are deliberately NOT counted as functions -- they're a separate syntactic form, and
    /// conflating them would inflate the "which functions must the replacement support"
    /// number this field exists to answer. See <see cref="Casts"/>.
    /// </summary>
    public List<string> Functions { get; init; } = [];

    /// <summary>SSIS cast operators used, e.g. "DT_WSTR", "DT_STR", "DT_I4" -- kept separate from <see cref="Functions"/> (see that field's note). The plan (§5.5) calls out <c>(DT_STR,n,codepage)</c> specifically as one with no clean .NET equivalent.</summary>
    public List<string> Casts { get; init; } = [];
}
