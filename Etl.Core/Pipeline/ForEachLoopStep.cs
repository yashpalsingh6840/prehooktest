using System.Diagnostics;
using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Pipeline;

/// <summary>
/// Runs <paramref name="buildSql"/> once per file matching <paramref name="action"/>'s own
/// Folder/FileSpec/Recurse -- the ONE loop-body shape the SSIS-to-C# generator supports for a
/// ForEach File Enumerator (see <see cref="ForEachFileLoopAction"/>'s own doc comment for why
/// every other shape is a generation gap instead of being modeled here): a single Execute SQL
/// Task whose text is rebuilt per iteration from the current file's own name/path.
///
/// Files are enumerated in ordinal-sorted order for determinism -- SSIS's own enumeration order
/// is filesystem-dependent and not something this step can reproduce exactly, so a stable,
/// repeatable order was chosen deliberately over an unreproducible one. Every iteration runs
/// inside the SAME transaction as every other step (the package's own shared
/// <see cref="IUnitOfWork"/>) -- one failing iteration rolls back every prior one, the same
/// all-or-nothing semantics every other step in a package already has.
/// </summary>
public sealed class ForEachLoopStep(string name, ForEachFileLoopAction action, Func<string, string> buildSql, ILogger<ForEachLoopStep> logger) : ILoadTask
{
    public string Name => name;

    public async Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var searchOption = action.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(action.Folder, action.FileSpec, searchOption)
            .OrderBy(path => path, StringComparer.Ordinal);

        long iterations = 0;
        foreach (var path in files)
        {
            var currentFile = action.NameMode switch
            {
                ForEachFileNameMode.NameAndExtension => Path.GetFileName(path),
                ForEachFileNameMode.NameOnly => Path.GetFileNameWithoutExtension(path),
                _ => path,
            };

            var sql = buildSql(currentFile);
            logger.LogInformation("{Step}: iteration for {File}: executing {Sql}", name, currentFile, sql);
            await uow.ExecuteSqlAsync(sql, ct);
            iterations++;
        }

        return new StepResult(name, 0, iterations, stopwatch.Elapsed);
    }
}
