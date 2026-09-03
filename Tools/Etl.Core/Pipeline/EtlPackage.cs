using Etl.Core.Abstractions;

namespace Etl.Core.Pipeline;

/// <summary>
/// A generic IEtlPackage -- a package's Control Flow is entirely data (a name, pre-load SQL,
/// pre-load file actions, and an ordered step list), so no per-package subclass is needed for
/// this shape. PreLoadFileActions defaults to empty -- every existing caller (hand-written or
/// generated) with no File System Task needs no change.
/// </summary>
public sealed record EtlPackage(
    string Name,
    IReadOnlyList<string> PreLoadStatements,
    IReadOnlyList<ILoadTask> Steps,
    IReadOnlyList<FileSystemPreLoadAction> PreLoadFileActions = null!,
    IReadOnlyList<FailureHandlerAction> FailureHandlers = null!) : IEtlPackage
{
    public IReadOnlyList<FileSystemPreLoadAction> PreLoadFileActions { get; init; } = PreLoadFileActions ?? [];

    public IReadOnlyList<FailureHandlerAction> FailureHandlers { get; init; } = FailureHandlers ?? [];
}
