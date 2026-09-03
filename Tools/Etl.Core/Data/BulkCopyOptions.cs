namespace Etl.Core.Data;

/// <summary>Mirrors a .dtsx OLE DB Destination's fast-load properties.</summary>
public sealed class BulkCopyOptions
{
    /// <summary>
    /// The PoC .dtsx files have no TABLOCK (FastLoadOptions is empty). We default this to true
    /// anyway: the pre-load TRUNCATE already holds a Sch-M lock for the whole transaction, so the
    /// table is exclusively ours regardless -- TableLock costs nothing here and enables the
    /// bulk-load fast path. Settable back to false for strict SSIS parity.
    /// </summary>
    public bool UseTableLock { get; init; } = true;

    /// <summary>Matches an empty FastLoadOptions (no CHECK_CONSTRAINTS hint).</summary>
    public bool CheckConstraints { get; init; }

    /// <summary>0 = single batch, matching FastLoadMaxInsertCommitSize=2147483647.</summary>
    public int BatchSize { get; init; }

    /// <summary>0 = no timeout, matching CommandTimeout=0.</summary>
    public int TimeoutSeconds { get; init; }

    public int NotifyAfter { get; init; } = 10_000;
}
