namespace Ssis.Extract.Codegen;

/// <summary>Builds the C# expression for a full-cache Lookup's joined column value, extracted
/// out of TransformEmitter's own per-column cascade so it is independently unit-testable with a
/// plain given-input/expected-output test.</summary>
internal static class LookupJoinExpressionBuilder
{
    /// <summary>Indexer, not TryGetValue -- a full-cache Lookup whose no-match output is not
    /// routed anywhere fails the component on a miss (SSIS's own default), so a
    /// KeyNotFoundException is the faithful translation. A Lookup that redirects no-match rows
    /// is a different, unsupported shape -- PackageGenerator gaps it rather than reaching here.</summary>
    public static string Build(LookupJoinSpec join, string referenceColumn) =>
        // join.InputColumnName (a SOURCE row property) and referenceColumn (a ReferenceRow
        // property, see LookupCacheEmitter's own identifierOf) are both raw external/pipeline
        // column names -- sanitized here, at the one place both are turned into C# identifiers.
        $"{join.CacheParameterName}[row.{PackageGenerator.SanitizeIdentifier(join.InputColumnName)}].{PackageGenerator.SanitizeIdentifier(referenceColumn)}";
}

/// <summary>One evidenced numeric-passthrough-coercion pairing, each measured against a real
/// dtexec run rather than guessed -- see CLAUDE.md's own "Numeric-passthrough-coercion" sections
/// for the evidence behind each. The generated SsisFn helper name is exactly the member's own
/// name, so adding a pairing here and to SsisFnEmitter is the only wiring needed. ONE exception:
/// <see cref="WidenI4ToNumeric"/> needed no dtexec probe at all -- int -> decimal is a strictly
/// widening, lossless C#-native implicit conversion (decimal has far more precision than a
/// 32-bit int could ever need), so there is no rounding/truncation/overflow question to
/// measure. See SsisFnEmitter's own WidenI4ToNumericBody doc comment for the real evidenced
/// instance (DailyETLMain.dtsx's own StockHolding_Staging.Last Cost Price).</summary>
public enum NumericCoercionKind
{
    NarrowR8ToI4,
    NarrowI8ToI4,
    NarrowNumericToI4,
    NarrowR4ToI4,
    ParseWstrToI4,
    WidenI4ToNumeric,
}

/// <summary>Builds the C# expression for one of the evidenced numeric-coercion pairings,
/// extracted out of TransformEmitter's own per-column cascade (previously five near-identical
/// inline string interpolations, one per pairing) so it is independently unit-testable.</summary>
internal static class NumericCoercionExpressionBuilder
{
    public static string Build(NumericCoercionKind kind, string columnName) =>
        $"SsisFn.{kind}(row.{columnName})";
}

/// <summary>Builds the C# expression for a plain passthrough column feeding a Flat File
/// Destination whose buffer type doesn't match its (always DT_WSTR) external column -- every
/// column a Flat File Destination writes is fundamentally text, so this widens via ToString()
/// rather than gapping, extracted out of TransformEmitter's own per-column cascade so it is
/// independently unit-testable.</summary>
internal static class FlatFileStringConversionExpressionBuilder
{
    /// <summary>A nullable-inferred source column needs a null-conditional ToString() (empty
    /// string for a genuine NULL) rather than a plain one, which would NullReferenceException.</summary>
    public static string Build(string columnName, bool isNullable) =>
        isNullable ? $"row.{columnName}?.ToString() ?? \"\"" : $"row.{columnName}.ToString()";
}
