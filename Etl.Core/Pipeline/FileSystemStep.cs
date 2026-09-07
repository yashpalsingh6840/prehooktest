using System.Diagnostics;
using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Pipeline;

/// <summary>
/// A File System Task positioned AFTER at least one Data Flow Task -- runs as an ordinary
/// step, unlike a pre-load one, which a generated <c>Program.cs</c> runs from its own pre-load
/// file-action list before any step at all (same pre-load/step split
/// <see cref="ExecuteSqlStep"/>'s own doc comment already describes for Execute SQL).
/// Row counts are always reported as zero -- a file operation has no rows.
/// </summary>
public sealed class FileSystemStep(string name, FileSystemPreLoadAction action, ILogger<FileSystemStep> logger) : ILoadTask
{
    public string Name => name;

    public async Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("{Step}: {Operation} {Source}{Destination}", name, action.Operation, action.SourcePath,
            action.DestinationPath is null ? "" : $" -> {action.DestinationPath}");
        await FileSystemActionRunner.RunAsync(action, ct);
        return new StepResult(name, 0, 0, stopwatch.Elapsed);
    }
}

/// <summary>
/// The one place a <see cref="FileSystemPreLoadAction"/> is actually executed -- shared by
/// <see cref="FileSystemStep"/> (post-flow position) and a generated <c>Program.cs</c>'s own
/// inlined pre-load file-action loop (pre-load position) so the operation-to-System.IO-call
/// mapping exists exactly once. Deliberately synchronous underneath (.NET has no async
/// File.Copy/Move/Delete) -- matches SSIS's own File
/// System Task, which blocks the control flow thread for the duration too.
/// </summary>
public static class FileSystemActionRunner
{
    public static Task RunAsync(FileSystemPreLoadAction action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (action.Operation)
        {
            case FileSystemOperation.Copy:
                File.Copy(action.SourcePath, RequireDestination(action), action.OverwriteDestination);
                break;

            case FileSystemOperation.Move or FileSystemOperation.Rename:
                var destination = RequireDestination(action);
                if (action.OverwriteDestination && File.Exists(destination)) File.Delete(destination);
                File.Move(action.SourcePath, destination);
                break;

            case FileSystemOperation.Delete:
                File.Delete(action.SourcePath);
                break;

            case FileSystemOperation.CreateDirectory:
                Directory.CreateDirectory(action.SourcePath);
                break;

            default:
                throw new NotSupportedException($"FileSystemOperation.{action.Operation} is not supported.");
        }
        return Task.CompletedTask;
    }

    private static string RequireDestination(FileSystemPreLoadAction action) =>
        action.DestinationPath ?? throw new InvalidOperationException(
            $"FileSystemOperation.{action.Operation} requires a destination path.");
}
