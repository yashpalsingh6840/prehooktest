namespace Etl.Core.Xml;

/// <summary>Mirrors an XML Source component's (<c>Microsoft.XmlSourceAdapter</c>) own settings
/// from a .dtsx file -- see <see cref="XmlRowSource{TRow}"/>'s own doc comment for the
/// row-element/column semantics this drives.</summary>
public sealed class XmlSourceOptions
{
    public required string FilePath { get; init; }

    /// <summary>The repeating element's own local name -- e.g. "record" for a
    /// <c>&lt;dataset&gt;&lt;record&gt;...&lt;/record&gt;...&lt;/dataset&gt;</c> file. Matched at
    /// ANY depth in the document (<c>XContainer.Descendants</c>), not just as a direct child of
    /// the root -- the one evidenced real shape nests it one level under a wrapper root element,
    /// and nothing about this component's own design ties the repeating element to a fixed
    /// depth.</summary>
    public required string RowElementName { get; init; }
}
