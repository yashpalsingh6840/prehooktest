using Etl.Core.Abstractions;

namespace Etl.Core.Pipeline;

/// <summary>Which <c>Microsoft.SCD</c> output(s) one incoming row is routed to.</summary>
[Flags]
public enum ScdRouting
{
    None = 0,
    Unchanged = 1,
    New = 2,
    FixedAttribute = 4,
    ChangingAttributeUpdates = 8,
    HistoricalAttributeInserts = 16,
}

/// <summary>
/// The pure, database-free classification half of <see cref="SlowlyChangingDimensionStep{TRow,TKey}"/>
/// -- separated out precisely so every measured rule below can be pinned by a plain unit test with
/// no SQL Server and no SSIS involved, the same way <c>MergeJoinRowSource</c>'s own join algorithm is.
///
/// <para><b>Every rule here was MEASURED against real SSIS via dtexec</b> (SQL Server 2022,
/// <c>.\SQLFORPOC_2022</c>, SSIS 160), not reasoned about, not read from documentation. The
/// measurement fixture wires all six SCD outputs to their own observation tables and seeds one
/// source row per change kind, so a single run reports the complete routing table by which table
/// each row lands in. Evidence, one line per rule:</para>
///
/// <list type="bullet">
/// <item><b>No current dimension row -> New.</b> A business key absent from the dimension entirely
/// (<c>EmpId</c> 103) routed to <c>New Output</c>; so did a key whose ONLY dimension row failed the
/// component's own <c>CurrentRowWhere</c> filter (<c>EmpId</c> 106 -- a row with <c>EndDate</c> set).
/// That second case is what proves the current-row filter is genuinely applied rather than being
/// decoration.</item>
///
/// <item><b>Fixed change wins outright.</b> A row changing only a <see cref="ScdColumnRole.Fixed"/>
/// column (<c>EmpId</c> 102) routed to <c>Fixed Attribute Output</c>. Two further rows, one changing
/// a fixed AND a historical column (<c>EmpId</c> 107) and one changing a fixed AND a changing column
/// (<c>EmpId</c> 108), BOTH routed to <c>Fixed Attribute Output</c> only -- so fixed takes precedence
/// over both other kinds, and is checked first here.</item>
///
/// <item><b><see cref="FailOnFixedAttributeChange"/> means FAIL, not "route elsewhere".</b> Re-running
/// the identical fixture with only that property flipped to <c>true</c> (its own schema default,
/// probe-confirmed) returned <c>DTSER_FAILURE</c> with error <c>0xC020803C</c> and SSIS's own text
/// "If the FailOnFixedAttributeChange property is set to TRUE, the transformation will fail when a
/// fixed attribute change is detected"; ALL SIX observation tables were empty afterwards, i.e. not
/// one row from that run landed anywhere. Hence <see cref="ScdFixedAttributeChangeException"/> rather
/// than any routing decision.</item>
///
/// <item><b>Historical beats changing, unless <see cref="UpdateChangingAttributeHistory"/>.</b> A row
/// changing only a historical column (<c>EmpId</c> 101) routed to <c>Historical Attribute Inserts
/// Output</c>; a row changing only a changing column (<c>EmpId</c> 104) routed to <c>Changing
/// Attribute Updates Output</c>. A row changing BOTH (<c>EmpId</c> 105) routed to <b>Historical
/// only</b> -- 7 output rows for 7 input rows. Re-running with only
/// <c>UpdateChangingAttributeHistory=true</c> routed that same row to <b>BOTH</b> outputs -- 8 output
/// rows for 7 input rows, every other row identical. That dual routing is exactly what the
/// component's own exclusion groups allow: <c>Historical Attribute Inserts Output</c> is
/// <c>exclusionGroup="2"</c> while all five others are group 1, so one row can reach one output from
/// each group.</item>
///
/// <item><b>Comparison is ORDINAL -- case- and whitespace-sensitive.</b> A row whose changing
/// attribute differed only in CASE (<c>'Fahmy'</c> vs <c>'FAHMY'</c>, <c>EmpId</c> 109) and a row
/// whose changing attribute differed only by a TRAILING SPACE (<c>'Aziz'</c> vs <c>'Aziz '</c>,
/// <c>EmpId</c> 110) were BOTH routed to <c>Changing Attribute Updates Output</c>, i.e. both counted
/// as real changes. This one matters disproportionately: SQL Server's own default collation is
/// case-insensitive AND trailing-space-insensitive, so a rewrite that pushed this comparison down
/// into SQL (a <c>MERGE</c>, say) would silently classify both of those rows as Unchanged. SSIS
/// compares in the pipeline buffer, and <see cref="StringComparison.Ordinal"/> is the faithful
/// translation.</item>
///
/// <item><b>Nothing differs -> Unchanged.</b> (<c>EmpId</c> 100.)</item>
/// </list>
///
/// <para><b>Not measured, therefore not implemented</b> (codegen gaps rather than guesses -- see
/// <c>PackagePlanner</c>'s own SCD resolution): inferred members
/// (<c>EnableInferredMember</c>/<c>InferredMemberIndicator</c>, both off/empty on the one real
/// evidenced package, so its <c>Inferred Member Updates Output</c> is unreachable and was never
/// exercised), and any <c>IncomingRowChangeType</c> other than 1.</para>
/// </summary>
public sealed class ScdClassifier
{
    private readonly ScdColumnRole[] _roles;

    public ScdClassifier(IReadOnlyList<ScdColumnRole> attributeRoles, bool failOnFixedAttributeChange, bool updateChangingAttributeHistory)
    {
        ArgumentNullException.ThrowIfNull(attributeRoles);
        if (attributeRoles.Any(r => r == ScdColumnRole.BusinessKey))
        {
            throw new ArgumentException(
                "attributeRoles describes the COMPARED columns only -- the business key is handled by the key selector and must not appear here.",
                nameof(attributeRoles));
        }

        _roles = [.. attributeRoles];
        FailOnFixedAttributeChange = failOnFixedAttributeChange;
        UpdateChangingAttributeHistory = updateChangingAttributeHistory;
    }

    public bool FailOnFixedAttributeChange { get; }
    public bool UpdateChangingAttributeHistory { get; }

    /// <summary>The number of compared attributes -- i.e. the required length of both the incoming
    /// and reference value arrays passed to <see cref="Classify"/>.</summary>
    public int AttributeCount => _roles.Length;

    /// <summary>
    /// Routes one incoming row. <paramref name="reference"/> is the matched CURRENT dimension row's
    /// own values in the same attribute order, or null when the business key had no current match at
    /// all.
    /// </summary>
    /// <exception cref="ScdFixedAttributeChangeException">
    /// A fixed attribute changed and <see cref="FailOnFixedAttributeChange"/> is set -- the measured
    /// behaviour of real SSIS, which fails the whole component rather than routing the row.
    /// </exception>
    public ScdRouting Classify(IReadOnlyList<object?> incoming, IReadOnlyList<object?>? reference, string businessKeyDescription)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (incoming.Count != _roles.Length)
            throw new ArgumentException($"expected {_roles.Length} attribute value(s), got {incoming.Count}", nameof(incoming));

        if (reference is null) return ScdRouting.New;

        if (reference.Count != _roles.Length)
            throw new ArgumentException($"expected {_roles.Length} reference value(s), got {reference.Count}", nameof(reference));

        var fixedChanged = false;
        var changingChanged = false;
        var historicalChanged = false;

        for (var i = 0; i < _roles.Length; i++)
        {
            if (ValuesEqual(incoming[i], reference[i])) continue;

            switch (_roles[i])
            {
                case ScdColumnRole.Fixed: fixedChanged = true; break;
                case ScdColumnRole.Changing: changingChanged = true; break;
                case ScdColumnRole.Historical: historicalChanged = true; break;
            }
        }

        // Fixed is checked FIRST, and that ordering is measured, not stylistic: a row with a fixed
        // change plus a historical one, and a row with a fixed change plus a changing one, both
        // routed to Fixed Attribute Output ONLY.
        if (fixedChanged)
        {
            return FailOnFixedAttributeChange
                ? throw new ScdFixedAttributeChangeException(businessKeyDescription)
                : ScdRouting.FixedAttribute;
        }

        if (historicalChanged)
        {
            // The one case where a row reaches two outputs -- and only when the package asks for it.
            return UpdateChangingAttributeHistory && changingChanged
                ? ScdRouting.HistoricalAttributeInserts | ScdRouting.ChangingAttributeUpdates
                : ScdRouting.HistoricalAttributeInserts;
        }

        if (changingChanged) return ScdRouting.ChangingAttributeUpdates;

        return ScdRouting.Unchanged;
    }

    /// <summary>
    /// Ordinal, case- and whitespace-sensitive for strings -- measured, see this type's own doc
    /// comment for the two probe rows that establish it. Everything else falls back to
    /// <see cref="object.Equals(object?, object?)"/>, which is value equality for every boxed
    /// primitive/<c>DateTime</c>/<c>decimal</c> a pipeline buffer column resolves to.
    /// </summary>
    internal static bool ValuesEqual(object? left, object? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is string a && right is string b) return string.Equals(a, b, StringComparison.Ordinal);
        return Equals(left, right);
    }
}

/// <summary>
/// A fixed attribute changed on a package whose <c>FailOnFixedAttributeChange</c> is set. Real SSIS
/// fails the whole component here (measured: <c>DTSER_FAILURE</c>, error <c>0xC020803C</c>, and no
/// rows landed in ANY output), so throwing -- which a generated <c>Program.cs</c> turns into a
/// rolled-back, failed run -- is the faithful translation.
/// </summary>
public sealed class ScdFixedAttributeChangeException(string businessKeyDescription)
    : InvalidOperationException(
        $"Slowly Changing Dimension: a fixed attribute changed for business key {businessKeyDescription}, " +
        "and FailOnFixedAttributeChange is set. Real SSIS fails the component in exactly this case. " +
        "To route these rows to the Fixed Attribute output instead, set FailOnFixedAttributeChange to false in the package.")
{
    public string BusinessKeyDescription { get; } = businessKeyDescription;
}
