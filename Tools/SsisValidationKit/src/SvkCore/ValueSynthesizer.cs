namespace Svk.Core;

/// <summary>
/// Produces deterministic, type-correct synthetic values from an SSIS pipeline column's own
/// canonical type name (the short lowercase vocabulary Ssis.Extract.Model.Shared.
/// SsisPipelineTypeMap resolves -- "wstr", "i4", ...). Seeded per (package, component, column,
/// row) rather than off one shared RNG stream, so adding a column/component elsewhere never
/// perturbs values already generated for an unrelated one -- required for "run twice with the
/// same --seed, diff byte-identical".
/// </summary>
public static class ValueSynthesizer
{
    public static Random RngFor(string package, string component, string column, int seed, int rowIndex)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in $"{package}{component}{column}")
            {
                hash ^= ch;
                hash *= 16777619u;
            }
            return new Random((int)hash ^ seed ^ (rowIndex * 397));
        }
    }

    /// <summary>Returns null for a value the row should carry as NULL (either because
    /// <paramref name="allowNull"/> rolled it, or because the type isn't in the map at all --
    /// callers treat an unmapped type as "cannot synthesize this column", the same "unknown
    /// returns null, never a guess" rule SsisPipelineTypeMap itself follows).</summary>
    public static object? Synthesize(string pipelineDataType, int? length, int? precision, int? scale, Random rng, bool allowNull, double nullProbability = 0.1)
    {
        if (allowNull && rng.NextDouble() < nullProbability) return null;

        return pipelineDataType.ToLowerInvariant() switch
        {
            "wstr" or "str" => SynthesizeString(length is > 0 ? length.Value : 20, rng),
            "text" or "ntext" => SynthesizeString(Math.Min(length is > 0 ? length.Value : 200, 200), rng),
            "i1" => (sbyte)rng.Next(-100, 100),
            "i2" => (short)rng.Next(-10_000, 10_000),
            "i4" => rng.Next(1, 100_000),
            "i8" => (long)rng.Next(1, 1_000_000),
            "ui1" => (byte)rng.Next(0, 255),
            "ui4" or "ui8" => (long)rng.Next(0, 100_000),
            "r4" => (float)Math.Round(rng.NextDouble() * 1000, 2),
            "r8" => Math.Round(rng.NextDouble() * 1000, 4),
            "numeric" or "decimal" => SynthesizeDecimal(precision is > 0 ? precision.Value : 18, scale is >= 0 ? scale.Value : 2, rng),
            "cy" => Math.Round((decimal)(rng.NextDouble() * 1000), 2),
            "bool" => rng.Next(2) == 1,
            "dbdate" => DateOnly.FromDateTime(DateTime.Today).AddDays(-rng.Next(0, 365)),
            "dbtimestamp" or "dbtimestamp2" => DateTime.Today.AddDays(-rng.Next(0, 365)).AddSeconds(rng.Next(0, 86_400)),
            "dbtimestampoffset" => new DateTimeOffset(DateTime.Today.AddDays(-rng.Next(0, 365)), TimeSpan.Zero),
            "dbtime" or "dbtime2" => TimeSpan.FromSeconds(rng.Next(0, 86_400)),
            "guid" => Guid.NewGuid(),
            _ => null,
        };
    }

    private static string SynthesizeString(int maxLength, Random rng)
    {
        const string letters = "abcdefghijklmnopqrstuvwxyz";
        var cap = Math.Max(1, Math.Min(maxLength, 12));
        var len = cap == 1 ? 1 : rng.Next(Math.Min(4, cap), cap + 1);
        var chars = new char[len];
        for (var i = 0; i < len; i++) chars[i] = letters[rng.Next(letters.Length)];
        chars[0] = char.ToUpperInvariant(chars[0]);
        return new string(chars);
    }

    private static decimal SynthesizeDecimal(int precision, int scale, Random rng)
    {
        scale = Math.Clamp(scale, 0, 6);
        var intDigits = Math.Clamp(precision - scale, 1, 9);
        var maxWhole = (long)Math.Pow(10, intDigits) - 1;
        var whole = rng.NextInt64(0, Math.Max(1, maxWhole));
        var scaleFactor = (decimal)Math.Pow(10, scale);
        var fraction = scale > 0 ? rng.Next(0, (int)scaleFactor) : 0;
        return whole + fraction / scaleFactor;
    }
}
