namespace Ssis.Extract.Model.Shared;

/// <summary>
/// A <c>DTS:ConnectionManager</c>, project- or package-scoped (plan §4.3). Covers the
/// generic shape every connection manager has, plus flat-file specifics when
/// <see cref="CreationName"/> is FLATFILE/MULTIFLATFILE.
/// </summary>
public sealed class ConnectionManagerSpec
{
    public required string ObjectName { get; init; }
    public string? RefId { get; init; }
    public string? DtsId { get; init; }
    public string? Description { get; init; }

    /// <summary>The connection manager type: OLEDB, FLATFILE, FILE, ADO.NET, etc.</summary>
    public required string CreationName { get; init; }

    /// <summary>"Project" or "Package".</summary>
    public required string Scope { get; init; }

    /// <summary>
    /// Connection string with any <c>Password=</c> segment stripped, regardless of
    /// <c>--no-redact</c> -- see <see cref="UnredactedConnectionString"/> for the opt-out.
    /// Null for connection managers with no static ConnectionString (e.g. one driven
    /// entirely by a PropertyExpression with no design-time default).
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>
    /// Only populated when the tool was run with <c>--no-redact</c>. Kept as a distinct
    /// property (rather than a flag that changes what <see cref="ConnectionString"/> means)
    /// so a spec.json's shape doesn't change between redacted and unredacted runs --
    /// only whether this field is present.
    /// </summary>
    public string? UnredactedConnectionString { get; init; }

    public required bool WasRedacted { get; init; }

    public ParsedConnectionString? Parsed { get; init; }

    public bool? DelayValidation { get; init; }

    /// <summary>Load-bearing semantics (shared transaction / #temp table visibility) -- see plan §4.3.</summary>
    public bool? RetainSameConnection { get; init; }

    public List<PropertyExpressionSpec> PropertyExpressions { get; init; } = [];

    /// <summary>Populated only when <see cref="CreationName"/> is FLATFILE or MULTIFLATFILE.</summary>
    public FlatFileFormatSpec? FlatFileFormat { get; init; }

    /// <summary>
    /// Names of sensitive child properties (e.g. <c>Password</c>) found with
    /// <c>Encrypted="1"</c> in the raw XML -- i.e. a <c>ProtectionLevel</c> that DPAPI-encrypts
    /// sensitive values (<c>EncryptSensitiveWithUserKey</c>) rather than stripping them at
    /// deploy time. The ciphertext itself is NEVER extracted (same redaction discipline as a
    /// plaintext <c>Password=</c> segment in <see cref="ConnectionString"/>) -- only that the
    /// value existed and is undecryptable outside the original author's Windows account. Empty
    /// when the connection manager carries no such node.
    /// </summary>
    public List<string> EncryptedProperties { get; init; } = [];
}

/// <summary>
/// <see cref="ConnectionManagerSpec.ConnectionString"/> parsed into its OLE DB/ADO.NET-style
/// key-value parts, so "which packages hit which server" is directly queryable instead of
/// living inside an opaque string. Best-effort: parses `Key=Value;` pairs generically and
/// promotes the well-known ones; provider-specific keys land in <see cref="Extras"/>.
/// </summary>
public sealed class ParsedConnectionString
{
    public string? Server { get; init; }
    public string? Database { get; init; }
    public string? Provider { get; init; }

    /// <summary>"Integrated" or "SqlLogin", derived from the presence of `User ID=`/`Integrated Security=`.</summary>
    public string? AuthMode { get; init; }
    public string? UserId { get; init; }
    public string? FilePath { get; init; }
    public Dictionary<string, string> Extras { get; init; } = [];
}

/// <summary>
/// A flat-file connection manager's file-format definition (plan §4.3) -- close to a
/// 1:1 mapping onto a CsvHelper/Sep reader configuration. Delimiters are exposed both
/// decoded (from SSIS's <c>_xHHHH_</c> escaping, see <see cref="XmlHexEscape"/>) and as a
/// display-friendly form, specifically so CLAUDE.md trap 2 (CRLF-vs-LF silent 0-row load)
/// is visible in the spec rather than hidden inside an escaped string.
/// </summary>
public sealed class FlatFileFormatSpec
{
    public string? Format { get; init; }
    public string? LocaleId { get; init; }
    public int? CodePage { get; init; }
    public bool? Unicode { get; init; }
    public int? HeaderRowsToSkip { get; init; }

    public string? HeaderRowDelimiterRaw { get; init; }
    public string? HeaderRowDelimiterDecoded { get; init; }
    public string? HeaderRowDelimiterDisplay { get; init; }

    public string? RowDelimiterRaw { get; init; }
    public string? RowDelimiterDecoded { get; init; }
    public string? RowDelimiterDisplay { get; init; }

    public bool? ColumnNamesInFirstDataRow { get; init; }

    public string? TextQualifierRaw { get; init; }
    public string? TextQualifierDecoded { get; init; }

    public List<FlatFileColumnSpec> Columns { get; init; } = [];
}

public sealed class FlatFileColumnSpec
{
    public required string ObjectName { get; init; }
    public string? DtsId { get; init; }
    public string? ColumnType { get; init; }

    public string? ColumnDelimiterRaw { get; init; }
    public string? ColumnDelimiterDecoded { get; init; }
    public string? ColumnDelimiterDisplay { get; init; }

    public required int DataTypeRaw { get; init; }
    public required string DataTypeName { get; init; }
    public int? MaximumWidth { get; init; }
    public int? DataPrecision { get; init; }
    public int? DataScale { get; init; }
    public bool? TextQualified { get; init; }
}
