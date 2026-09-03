namespace Ssis.Extract.Codegen.Tests;

public class SsisFnEmitterTests
{
    [Fact]
    public void Emit_ProducesNoFile_WhenNothingIsUsed()
    {
        var result = SsisFnEmitter.Emit("LoadEmployees.Ssis", new HashSet<string>());

        Assert.Empty(result.Files);
        Assert.Empty(result.Gaps);
    }

    [Fact]
    public void Emit_MatchesLoadEmployeesShape_UpperSubstringAndStr()
    {
        var result = SsisFnEmitter.Emit("LoadEmployees.Ssis", new HashSet<string> { "Upper", "Substring", "Str" });

        var file = Assert.Single(result.Files);
        Assert.Equal("Ssis/SsisFn.cs", file.RelativePath);
        Assert.Contains("using System.Globalization;", file.Content);
        Assert.Contains("public static string Upper(string value) => value.ToUpperInvariant();", file.Content);
        Assert.Contains("public static string Substring(string value, int oneBasedStart, int length)", file.Content);
        Assert.Contains("public static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);", file.Content);

        // The measured divergence this whole emitter exists to encode correctly.
        Assert.Contains("if (oneBasedStart < 1)", file.Content);
        Assert.Contains("throw new ArgumentOutOfRangeException", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_MatchesLoadReferenceDataShape_UpperAndStrOnly_NoSubstring()
    {
        var result = SsisFnEmitter.Emit("LoadReferenceData.Ssis", new HashSet<string> { "Upper", "Str" });

        var file = Assert.Single(result.Files);
        Assert.Contains("public static string Upper", file.Content);
        Assert.Contains("public static string Str", file.Content);
        Assert.DoesNotContain("Substring", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ProducesBothDateDiffDaysOverloads_ForTheDateOnlyAndDateTimeShapes()
    {
        // Two overloads because a DATE-typed SQL column reference generates as DateOnly
        // (SqlRowReaderEmitter/EntityEmitter's own convention) while GETDATE()/GETUTCDATE()
        // always maps to ctx.LoadedAtUtc, a DateTime -- the real evidenced call,
        // DATEDIFF("dd",SignupDate,GETDATE()), mixes both.
        var result = SsisFnEmitter.Emit("Package.Ssis", new HashSet<string> { "DateDiffDays" });

        var file = Assert.Single(result.Files);
        Assert.Contains("public static int DateDiffDays(DateTime start, DateTime end)", file.Content);
        Assert.Contains("public static int DateDiffDays(DateOnly start, DateTime end)", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_OmitsTheGlobalizationUsing_WhenStrIsNotNeeded()
    {
        var result = SsisFnEmitter.Emit("LoadReferenceData.Ssis", new HashSet<string> { "Upper" });

        var file = Assert.Single(result.Files);
        Assert.DoesNotContain("using System.Globalization;", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ProducesTheThreeSpeculativeDataConversionTargets_R8BoolWstr()
    {
        // Built speculatively 2026-08-28 -- no real package needs any of these yet. Each body's
        // exact behavior is measured against a real dtexec run
        // (synthetic-data-conversion-types-tables.sql), not guessed -- see DataConvertPayload's
        // own doc comment for the full account.
        var result = SsisFnEmitter.Emit("Package.Ssis", new HashSet<string> { "ToNullableR8", "ToNullableBool", "ToWstr" });

        var file = Assert.Single(result.Files);
        Assert.Contains("using System.Globalization;", file.Content);
        Assert.Contains("public static double? ToNullableR8(string? value) =>", file.Content);
        Assert.Contains("public static bool? ToNullableBool(string? value)", file.Content);
        // The one genuinely different failure mode: truncates, never nulls out.
        Assert.Contains("public static string? ToWstr(string? value, int maxLength) =>", file.Content);
        Assert.Contains("value[..maxLength]", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ProducesThreeMoreSpeculativeDataConversionTargets_I2I8DbTimestamp()
    {
        // Built speculatively 2026-08-30 -- no real package needs any of these yet. Each body's
        // exact behavior is measured against a real dtexec run
        // (synthetic-data-conversion-types2-tables.sql), not guessed: all three follow the same
        // "NULL on any failure" convention as ToNullableI4/ToNullableDate/ToNullableR8/
        // ToNullableBool, not DT_WSTR's own different truncation behavior.
        var result = SsisFnEmitter.Emit("Package.Ssis", new HashSet<string> { "ToNullableI2", "ToNullableI8", "ToNullableDateTime" });

        var file = Assert.Single(result.Files);
        Assert.Contains("using System.Globalization;", file.Content);
        Assert.Contains("public static short? ToNullableI2(string? value) =>", file.Content);
        Assert.Contains("public static long? ToNullableI8(string? value) =>", file.Content);
        Assert.Contains("public static DateTime? ToNullableDateTime(string? value) =>", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ProducesParseWstrToI4_ForTheStringToIntPassthroughPairing()
    {
        // Measured 2026-08-30 against a real dtexec run
        // (synthetic-string-to-int-coercion-tables.sql): the same round-to-nearest-ties-to-even
        // rule as NarrowR8ToI4, but unlike that pairing the FAILURE mode is also measured --
        // non-numeric/empty/whitespace-only/out-of-range text fails the real probe hard, so this
        // throws to match rather than nulling out.
        var result = SsisFnEmitter.Emit("Package.Ssis", new HashSet<string> { "ParseWstrToI4" });

        var file = Assert.Single(result.Files);
        Assert.Contains("using System.Globalization;", file.Content);
        Assert.Contains("public static int? ParseWstrToI4(string? value) =>", file.Content);
        Assert.Contains("double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ProducesTheThreeSpeculativeNumericToI4Coercions_I8NumericR4()
    {
        // Built speculatively 2026-08-30 (synthetic-int-numeric-coercion-tables.sql) -- no real
        // evidenced instance of any of the three anywhere in the tracked portfolio. A real
        // dtexec run measured the same round-to-nearest-ties-to-even rule as NarrowR8ToI4 for
        // both float-shaped pairings (numeric, r4); i8->i4 has no rounding question at all.
        var result = SsisFnEmitter.Emit("Package.Ssis", new HashSet<string> { "NarrowI8ToI4", "NarrowNumericToI4", "NarrowR4ToI4" });

        var file = Assert.Single(result.Files);
        Assert.Contains("public static int NarrowI8ToI4(long value) => checked((int)value);", file.Content);
        Assert.Contains("public static int? NarrowI8ToI4(long? value)", file.Content);
        Assert.Contains("public static int NarrowNumericToI4(decimal value) => checked((int)Math.Round(value));", file.Content);
        Assert.Contains("public static int? NarrowNumericToI4(decimal? value)", file.Content);
        Assert.Contains("public static int NarrowR4ToI4(float value) => checked((int)Math.Round((double)value));", file.Content);
        Assert.Contains("public static int? NarrowR4ToI4(float? value)", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }
}
