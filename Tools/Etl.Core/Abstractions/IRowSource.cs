namespace Etl.Core.Abstractions;

/// <summary>A Flat File Source (or any other producer of raw pipeline rows).</summary>
public interface IRowSource<out TRow>
{
    string Name { get; }

    IAsyncEnumerable<TRow> ReadAsync(CancellationToken ct);
}
