namespace Ssis.Extract.ObjectModel;

/// <summary>
/// The structural facts plan §7.3 names as independently confirmable from the real SSIS
/// object model: executable count/names, connection manager count/types, variable count,
/// parameter count/types, pipeline component/path counts, precedence constraint count. Every
/// count here is a RECURSIVE total (walks every container's own <c>Executables</c>), not
/// just top-level -- neither PoC package nests a container today, but a client package with
/// a Sequence/ForEach Loop should still be checked correctly, not silently under-counted.
///
/// Plain settable properties, not <c>required</c>/<c>init</c> -- net48 has no runtime support
/// for either (no <c>IsExternalInit</c>/<c>RequiredMemberAttribute</c> in its BCL), and this
/// is an internal DTO with one constructor site (<see cref="PackageOracle.Load"/>), not a
/// public API worth a source-generator shim to get compile-time enforcement back.
/// </summary>
public sealed class PackageOracleFacts
{
    public int ExecutableCount { get; set; }
    public List<string> ExecutableNames { get; set; } = new();
    public int ConnectionManagerCount { get; set; }
    public List<string> ConnectionManagerCreationNames { get; set; } = new();
    public int VariableCount { get; set; }
    public int ParameterCount { get; set; }
    public List<string> ParameterDataTypeNames { get; set; } = new();
    public int PipelineComponentCount { get; set; }
    public int PipelinePathCount { get; set; }
    public int PrecedenceConstraintCount { get; set; }
}
