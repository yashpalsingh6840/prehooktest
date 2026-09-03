using System.Reflection;
using System.Text;
using Etl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Etl.Core.Data;

/// <summary>
/// A Flat File Destination. Unlike <see cref="SqlBulkSink{TEntity}"/>, this never touches
/// <paramref name="uow"/> -- a flat file write has no transactional participant of its own, the
/// same separation <see cref="SqlRowSource{TRow}"/> already has from the load transaction on the
/// read side. Properties are read by NAME (never ordinal, same rule <see cref="SqlBulkSink{TEntity}"/>
/// follows), matched once at construction against <see cref="FlatFileColumnFormat.PropertyName"/>
/// in <see cref="Columns"/>' own order -- that order IS the physical file layout, resolved by
/// codegen from the flat file connection manager's own column list, not the entity's C# property
/// declaration order.
///
/// Fixed-width padding (pad right with spaces) and truncation (silent, never error) were
/// verified end to end against a real generated run -- SyntheticFlatFileDestination.dtsx seeds
/// a FullName value 8 characters longer than its own FixedWidth column width, and the actual
/// written file byte-for-byte matches this: right-padded with spaces when short, truncated (not
/// wrapped/erroring) when long. See CLAUDE.md's Flat File Destination section for the exact
/// bytes.
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
        var resolved = columns
            .Select(c => (Format: c, Property: typeof(TEntity).GetProperty(c.PropertyName, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"{typeof(TEntity).Name} has no public property '{c.PropertyName}'")))
            .ToArray();

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // FileMode.Create truncates-and-recreates for Overwrite=true; Append preserves existing
        // content otherwise -- the two real evidenced instances (RBC_Demo_ETL's own
        // DFT_ExportDelimited/DFT_ExportFixedWidth) both set Overwrite=true.
        await using var stream = new FileStream(filePath, overwrite ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (!string.IsNullOrEmpty(headerLine))
            await writer.WriteAsync(headerLine);

        long written = 0;
        await foreach (var row in rows.WithCancellation(ct))
        {
            foreach (var (format, property) in resolved)
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
