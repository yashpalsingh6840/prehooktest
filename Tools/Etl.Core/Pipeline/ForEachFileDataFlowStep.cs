using System.Diagnostics;
using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Pipeline;

/// <summary>
/// A ForEach Loop Container over files whose body is one Data Flow Task, re-run once per
/// enumerated file -- the second loop-body shape this generator supports, alongside
/// <see cref="ForEachLoopStep"/>'s single-Execute-SQL-Task body. Built speculatively 2026-08-30:
/// no real evidenced package anywhere in the tracked portfolio has this shape (RBC_Demo_ETL's
/// own FEL_SampleFiles, the one real ForEach Loop with a real Data Flow Task package alongside
/// it, uses the SQL-per-iteration shape instead) -- surfaced to the user directly before
/// building, same sign-off discipline as every other speculative round in this codebase.
///
/// Each iteration constructs a fresh <see cref="IRowSource{TRow}"/> for that file's own path via
/// <paramref name="sourceFactory"/> and runs it through the SAME <paramref name="transform"/> and
/// <paramref name="sink"/> every other iteration uses (both are stateless/reusable, resolved once
/// via DI by the generated Program.cs) -- via a fresh <see cref="DataFlowStep{TRow,TEntity}"/>
/// instance per file, reusing that type's own streaming Source-&gt;Transform-&gt;Sink loop and its
/// own "zero rows read is an error" guard rather than duplicating either. Files are enumerated
/// the same ordinal-sorted order as <see cref="ForEachLoopStep"/>, for the same reason (SSIS's
/// own enumeration order is filesystem-dependent and not reproducible). Every iteration runs
/// inside the SAME transaction as every other step -- one failing file rolls back every prior
/// one and every other step in the package, the same all-or-nothing semantics
/// <see cref="ForEachLoopStep"/> already has.
/// </summary>
public sealed class ForEachFileDataFlowStep<TRow, TEntity>(
    string name,
    ForEachFileLoopAction action,
    Func<string, IRowSource<TRow>> sourceFactory,
    IRowTransform<TRow, TEntity> transform,
    IBulkSink<TEntity> sink,
    ILogger<DataFlowStep<TRow, TEntity>> stepLogger) : ILoadTask
    where TEntity : class
{
    public string Name => name;

    public async Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var searchOption = action.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(action.Folder, action.FileSpec, searchOption)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        long totalRead = 0, totalWritten = 0;
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            var currentFile = action.NameMode switch
            {
                ForEachFileNameMode.NameAndExtension => Path.GetFileName(path),
                ForEachFileNameMode.NameOnly => Path.GetFileNameWithoutExtension(path),
                _ => path,
            };

            var source = sourceFactory(currentFile);
            var iterationStep = new DataFlowStep<TRow, TEntity>($"{name}[{Path.GetFileName(path)}]", source, transform, sink, stepLogger);
            var result = await iterationStep.RunAsync(uow, load, ct);
            totalRead += result.RowsRead;
            totalWritten += result.RowsWritten;
        }

        return new StepResult(name, totalRead, totalWritten, stopwatch.Elapsed);
    }
}
