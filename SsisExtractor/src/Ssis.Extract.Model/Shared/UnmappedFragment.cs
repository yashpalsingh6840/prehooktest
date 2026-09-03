namespace Ssis.Extract.Model.Shared;

/// <summary>
/// One piece of source XML the typed model does not (yet) understand, kept verbatim so
/// nothing is silently dropped (plan §2.3 "lossless"). Slice 1 only models identity,
/// provenance, connection managers, variables, and parameters -- everything else at the
/// package level (control flow, event handlers, precedence constraints, design-time
/// layout) lands here until slices 2-3 give it a typed home. The coverage-percentage
/// metric (plan §7.2) is computed from how much of a package ends up in this bag; that
/// computation itself is a slice-2 deliverable, so for now this is just honest storage.
/// </summary>
public sealed class UnmappedFragment
{
    /// <summary>Where this was found, e.g. "Package/Executables" or "Package/DesignTimeProperties".</summary>
    public required string Location { get; init; }

    /// <summary>Why it's unmapped -- which build slice is expected to model it.</summary>
    public required string Reason { get; init; }

    /// <summary>The element's outer XML, verbatim.</summary>
    public required string RawXml { get; init; }
}
