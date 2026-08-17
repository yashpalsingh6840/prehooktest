namespace Ssis.Extract.Model.Analysis;

/// <summary>
/// Weights for <c>Ssis.Extract.Dtsx.ComplexityScorer</c> (plan §5.6: "Weights live in a
/// config file, not in code, so they can be tuned once the client's real distribution is
/// visible"). <see cref="Default"/> mirrors <c>config/complexity-weights.json</c> exactly --
/// that file is the actual tunable config; the embedded default just means <c>ssisx report</c>
/// works with no flags out of the box. Override with <c>--weights &lt;path.json&gt;</c>.
/// </summary>
public sealed class ComplexityWeights
{
    public double Task { get; init; } = 1;
    public double Container { get; init; } = 3;
    public double DataFlowTask { get; init; } = 2;
    public double PipelineComponent { get; init; } = 1;
    public double Expression { get; init; } = 1;
    public double SqlStatement { get; init; } = 2;
    public double ScriptTask { get; init; } = 15;
    public double Loop { get; init; } = 5;
    public double EventHandler { get; init; } = 4;
    public double UnmappedNode { get; init; } = 10;

    /// <summary>Score at or below this is "Simple" (unless a higher-priority classification rule fires first -- see <c>ComplexityScorer</c>'s doc comment for the exact order).</summary>
    public double SimpleThreshold { get; init; } = 15;

    /// <summary>Score at or above this is "HighRisk" regardless of composition.</summary>
    public double HighRiskThreshold { get; init; } = 60;

    public static readonly ComplexityWeights Default = new();
}

/// <summary>
/// Per-package complexity counts (plan §5.6), the weighted <see cref="Score"/> derived from
/// them via <see cref="ComplexityWeights"/>, and the resulting <see cref="Classification"/>
/// (Simple / SqlHeavy / Procedural / HighRisk). All counts are recursive across the whole
/// control-flow tree (containers, nested executables, event handlers) and every Data Flow
/// Task's pipeline.
/// </summary>
public sealed class ComplexityStats
{
    /// <summary>Every leaf executable (a task, not a container) at any depth.</summary>
    public required int TaskCount { get; init; }

    /// <summary>Every executable with at least one child (Sequence, ForEach/For Loop -- none in this PoC).</summary>
    public required int ContainerCount { get; init; }

    public required int DataFlowTaskCount { get; init; }
    public required int PipelineComponentCount { get; init; }

    /// <summary>Property expressions (package + every executable + every connection manager) plus precedence-constraint expressions plus pipeline output-column expressions -- every place plan §5.5 lists as an expression source, before that slice's full harvest/tokenization exists.</summary>
    public required int ExpressionCount { get; init; }

    public required int SqlStatementCount { get; init; }
    public required int ScriptTaskCount { get; init; }

    /// <summary>Script Component pipeline transforms, counted separately from <see cref="ScriptTaskCount"/> since they're control-flow vs. data-flow, but weighted identically (both are "arbitrary code, no structural model") and both drive <c>HighRisk</c> classification unconditionally -- see <c>ComplexityScorer.Classify</c>.</summary>
    public required int ScriptComponentCount { get; init; }

    /// <summary>ForEach/For Loop containers specifically (a subset of <see cref="ContainerCount"/>) -- called out separately per plan §5.6 since loops are a specific rewrite-effort signal, not just "any container."</summary>
    public required int LoopCount { get; init; }

    public required int EventHandlerCount { get; init; }

    /// <summary>Tasks with no typed payload (<c>UnmappedTaskPayload</c>) -- an unrecognized executable type this build slice hasn't modeled. Always 0 for both PoC packages (100% coverage as of slice 3); a real client package is where this stops being zero.</summary>
    public required int UnmappedNodeCount { get; init; }

    public required double Score { get; init; }

    /// <summary>"Simple" | "SqlHeavy" | "Procedural" | "HighRisk" -- see <c>ComplexityScorer.Classify</c> for the exact rule order.</summary>
    public required string Classification { get; init; }
}
