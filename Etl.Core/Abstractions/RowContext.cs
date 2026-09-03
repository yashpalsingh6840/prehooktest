namespace Etl.Core.Abstractions;

/// <summary>
/// Per-row ambient data handed to an <see cref="IRowTransform{TRow,TEntity}"/>.
/// <see cref="LoadedAtUtc"/> is stamped once per load (not once per row, unlike SSIS's
/// per-row GETUTCDATE()) so every row in a run shares one deterministic instant.
/// </summary>
public readonly record struct RowContext(long RowNumber, DateTime LoadedAtUtc, string SourceName);
