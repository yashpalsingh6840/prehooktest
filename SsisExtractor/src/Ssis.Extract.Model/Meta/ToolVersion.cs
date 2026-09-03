namespace Ssis.Extract.Model.Meta;

/// <summary>
/// Recorded into every <c>_meta.json</c> so a spec.json can always be traced back to the
/// extractor version that produced it (relevant once the JSON contract starts changing
/// across build slices -- see docs/spec-schema.md).
/// </summary>
public static class ToolVersion
{
    public const string Current = "0.1.0-slice5";
}
