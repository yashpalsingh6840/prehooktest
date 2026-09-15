using System.Reflection;
using System.Text;
using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Data;

/// <summary>
/// A Flat File Destination. Not transactional -- never touches <paramref name="uow"/>, since a
/// file write has no transaction to join (same read-side separation as <see cref="SqlRowSource{TRow}"/>).
/// Columns are matched by NAME (never ordinal) against each entity property, in the order the
/// flat file connection manager declares them -- that order is the real file layout, which need
/// not match the entity's own C# property order. A fixed-width column pads a short value with
/// spaces and silently truncates a long one (never errors), matching real SSIS behavior --
/// verified against a real run; see CLAUDE.md's Flat File Destination section for specifics.
/// </summary>
public sealed class FlatFileBulkSink<TEntity>(
    string filePath,
    bool overwrite,
    string? headerLine,
    IReadOnlyList<FlatFileColumnFormat> columns,
    ILogger<FlatFileBulkSink<TEntity>> logger) : IBulkSink<TEntity>
    where TEntity : class
{
    public async Task<long> WriteAsync(IUnitOfWork uow, IAsyncEnumerable<TEntity> rows, CancellationToken ct)
    {
        var resolvedColumns = columns
            .Select(c => (Format: c, Property: typeof(TEntity).GetProperty(c.PropertyName, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"{typeof(TEntity).Name} has no public property '{c.PropertyName}'")))
            .ToArray();

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Overwrite=true recreates the file (FileMode.Create); otherwise append to it.
        await using var stream = new FileStream(filePath, overwrite ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (!string.IsNullOrEmpty(headerLine))
            await writer.WriteAsync(headerLine);

        long written = 0;
        await foreach (var row in rows.WithCancellation(ct))
        {
            foreach (var (format, property) in resolvedColumns)
            {
                var raw = property.GetValue(row)?.ToString() ?? "";
                var text = format.FixedWidth is { } width
                    ? (raw.Length >= width ? raw[..width] : raw.PadRight(width))
                    : raw;
                await writer.WriteAsync(text);
                await writer.WriteAsync(format.Delimiter);
            }

            written++;
        }

        await writer.FlushAsync(ct);

        logger.LogInformation("{File}: flat file write complete, {Rows:N0} rows", filePath, written);
        return written;
    }
}
