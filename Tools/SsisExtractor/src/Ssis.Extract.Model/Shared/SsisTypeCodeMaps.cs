namespace Ssis.Extract.Model.Shared;

/// <summary>
/// Resolves the numeric "DataType" codes that appear in SSIS project/package XML to
/// friendly names. CLAUDE.md trap 12 (this PoC repo) documents that these are NOT one
/// enum reused everywhere -- there are (at least) three distinct numeric type spaces in
/// play across the files this extractor reads, and conflating them silently produces
/// wrong labels. Each gets its own lookup table below; never share one across contexts.
/// </summary>
public static class SsisTypeCodeMaps
{
    /// <summary>
    /// Context: <c>Project.params</c>' <c>SSIS:Property SSIS:Name="DataType"</c>, and the
    /// equivalent <c>SSIS:PackageMetaData/SSIS:Parameters/.../DataType</c> block inside
    /// <c>.dtproj</c>. Verified: this is literally the ordinal of .NET's <see cref="System.TypeCode"/>
    /// (18 = String, seen on every parameter in both PoC packages' Project.params/.dtproj).
    /// Authoritative -- System.TypeCode's ordinals are a stable BCL contract, not something
    /// to hand-maintain a lookup table for.
    /// </summary>
    public static string ProjectParamsDataTypeName(int code)
    {
        if (Enum.IsDefined(typeof(TypeCode), code))
        {
            return ((TypeCode)code).ToString();
        }
        return $"Unknown({code})";
    }

    /// <summary>
    /// Context: <c>DTS:DataType</c> on a <c>&lt;DTS:VariableValue&gt;</c> or
    /// <c>&lt;DTS:PackageParameter&gt;</c>/nested <c>ParameterValue</c> property inside a
    /// <c>.dtsx</c> file -- the *declared* type of a Variable or (package-deployment-model)
    /// parameter. This is a different numeric space from the pipeline DT_* buffer types
    /// below (evidence: code 8 appears here for String, but DT_WSTR is 130 in the pipeline
    /// enum -- see <see cref="PipelineDataTypeName"/>). It lines up with the classic OLE
    /// Automation VARIANT type codes (VT_*), which is what SSIS's runtime object model
    /// (<c>Microsoft.SqlServer.Dts.Runtime.Variable.DataType</c>) is documented to use
    /// underneath. Only entries actually evidenced in this repo's fixtures, or unambiguous
    /// well-known VARENUM values, are included -- anything else renders as "Unknown(n)"
    /// rather than a guessed label. Extend this table only after confirming a new code
    /// against the runtime object model (the same "ask the runtime, don't guess the XML"
    /// approach as CLAUDE.md trap 12's own resolution), e.g. via the object-model oracle
    /// planned for slice 6.
    /// </summary>
    public static string VariantDeclaredTypeName(int code) => code switch
    {
        0 => "Empty",
        1 => "Null",
        2 => "Int16",
        3 => "Int32",
        4 => "Single",
        5 => "Double",
        6 => "Currency",
        7 => "DateTime",
        8 => "String",       // evidenced: SourceFilePath/DepartmentFilePath/DesignationFilePath variables, all package parameters
        11 => "Boolean",
        14 => "Decimal",
        16 => "SByte",
        17 => "Byte",
        18 => "UInt16",
        19 => "UInt32",
        20 => "Int64",
        21 => "UInt64",
        _ => $"Unknown({code})",
    };

    /// <summary>
    /// Context: <c>DTS:FlatFileColumn</c>'s <c>DTS:DataType</c> attribute -- the SSIS
    /// pipeline buffer type enumeration (DT_*). Cross-validated against this repo's own
    /// fixtures: FlatFileColumn DataType=3/130/131/133 line up exactly with the textual
    /// <c>dataType="i4"/"wstr"/"numeric"/"dbDate"</c> attributes the pipeline XML uses for
    /// the *same* physical columns elsewhere in the same file (EmployeeID, FirstName,
    /// Salary, HireDate respectively). The remaining entries are the standard published
    /// DT_* table (Microsoft "Integration Services Data Types" reference) and are lower-
    /// confidence than the four cross-validated ones -- treat as best-effort.
    /// </summary>
    public static string PipelineDataTypeName(int code) => code switch
    {
        0 => "DT_EMPTY",
        1 => "DT_NULL",
        2 => "DT_I2",
        3 => "DT_I4",           // evidenced (EmployeeID)
        4 => "DT_R4",
        5 => "DT_R8",
        6 => "DT_CY",
        7 => "DT_DATE",
        11 => "DT_BOOL",
        14 => "DT_DECIMAL",
        16 => "DT_I1",
        17 => "DT_UI1",
        18 => "DT_UI2",
        19 => "DT_UI4",
        20 => "DT_I8",
        21 => "DT_UI8",
        64 => "DT_FILETIME",
        72 => "DT_GUID",
        128 => "DT_BYTES",
        129 => "DT_STR",
        130 => "DT_WSTR",       // evidenced (FirstName/LastName/Department/City/State)
        131 => "DT_NUMERIC",    // evidenced (Salary)
        132 => "DT_IMAGE",
        133 => "DT_DBDATE",     // evidenced (HireDate)
        134 => "DT_DBTIME",
        135 => "DT_TEXT",
        136 => "DT_NTEXT",
        137 => "DT_DBTIMESTAMP",
        138 => "DT_DBTIMESTAMP2",
        139 => "DT_DBTIME2",
        140 => "DT_DBTIMESTAMPOFFSET",
        _ => $"Unknown({code})",
    };

    /// <summary>
    /// Context: numeric <c>DTS:ProtectionLevel</c> on a Package or (via the .dtproj
    /// manifest / Project element) a Project. This is the well-documented, stable
    /// <c>Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel</c> enum.
    /// </summary>
    public static string ProtectionLevelName(int code) => code switch
    {
        0 => "DontSaveSensitive",
        1 => "EncryptSensitiveWithUserKey",
        2 => "EncryptSensitiveWithPassword",
        3 => "EncryptAllWithPassword",
        4 => "EncryptAllWithUserKey",
        5 => "ServerStorage",
        _ => $"Unknown({code})",
    };
}
