using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Model.Package;

/// <summary>
/// A <c>DTS:EventHandler</c> (plan §4.6) -- OnError/OnTaskFailed/OnPreExecute/OnPostExecute/
/// OnWarning/OnVariableValueChanged/etc. Structurally a container just like
/// <see cref="ExecutableSpec"/> (its own Variables/Executables/PrecedenceConstraints),
/// walked with the same recursive code. Absent from both of this PoC's own packages, so this
/// was wired generically and carried an "unverified against a real example" caveat for a long
/// time.
///
/// <b>Now CONFIRMED against a real one (2026-09-02):</b> the third-party
/// Package_Advanced.dtsx carries an OnError handler containing an Execute SQL Task, and it
/// extracts correctly -- <c>EventName='OnError'</c>, one child with its SQL resolved. Note that
/// real element carries NO <c>DTS:ObjectName</c> at all (its attributes are refId,
/// CreationName, DTSID, EventID, EventName, LocaleID), so the event name came from the
/// FALLBACK path <see cref="EventName"/> documents rather than the primary one -- i.e. the
/// primary read would have failed on the first real package it ever met, and the fallback is
/// what makes this work.
///
/// Codegen does NOT translate event handlers; <c>PackagePlanner.ReportEventHandlers</c> reports
/// each one that contains work, because before that they were omitted from generated output in
/// complete silence.
/// </summary>
public sealed class EventHandlerSpec
{
    /// <summary>
    /// The event this handles, e.g. "OnError". Read from <c>DTS:ObjectName</c> -- every
    /// real-world .dtsx sample seen (outside this PoC) uses the event name as the
    /// EventHandler's ObjectName -- with a fallback attempt at a literal
    /// <c>DTS:EventName</c> attribute if a future SSIS version turns out to use one
    /// instead. Unverified against this PoC's fixtures (neither has an event handler).
    /// </summary>
    public required string EventName { get; init; }

    public required string RefId { get; init; }
    public string? ObjectName { get; init; }
    public string? DtsId { get; init; }
    public string? Description { get; init; }
    public bool? Disabled { get; init; }

    public List<VariableSpec> Variables { get; init; } = [];
    public List<ExecutableSpec> Children { get; init; } = [];
    public List<PrecedenceConstraintSpec> PrecedenceConstraints { get; init; } = [];
    public ControlFlowDagSpec Dag { get; init; } = new();
}
