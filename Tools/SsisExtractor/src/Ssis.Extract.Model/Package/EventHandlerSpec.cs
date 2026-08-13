using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Model.Package;

/// <summary>
/// A <c>DTS:EventHandler</c> (plan §4.6) -- OnError/OnTaskFailed/OnPreExecute/OnPostExecute/
/// OnWarning/OnVariableValueChanged/etc. Structurally a container just like
/// <see cref="ExecutableSpec"/> (its own Variables/Executables/PrecedenceConstraints),
/// walked with the same recursive code. Not present in either of this PoC's two packages
/// -- wired generically because it's explicit plan scope for this build slice and the
/// container-walking code is already generic, but genuinely unverified against a real
/// example; treat the exact attribute names here (particularly which attribute actually
/// carries the event name -- see <see cref="EventName"/>) as best-effort until confirmed
/// against a client package that has one.
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
