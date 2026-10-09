namespace Ssis.Extract.Codegen;

/// <summary>Where a generated package reads and writes its files (<c>ssisx generate --storage</c>).
/// One mode per package, never mixed.</summary>
public enum StorageMode
{
    /// <summary>The local filesystem (the default).</summary>
    Local,

    /// <summary>An S3 bucket, configured by the "Storage" appsettings section.</summary>
    S3,
}
