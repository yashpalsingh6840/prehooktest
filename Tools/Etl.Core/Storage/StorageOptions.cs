namespace Etl.Core.Storage;

/// <summary>
/// The "Storage" configuration section, read only by a package generated with <c>--storage s3</c>.
/// Credentials are never configured here -- the AWS SDK's default chain (environment variables,
/// shared profile, IAM role) supplies them, same rule as the database password.
/// </summary>
public sealed class StorageOptions
{
    /// <summary>The S3 bucket every file path in the package is resolved under.</summary>
    public string Bucket { get; init; } = "";

    /// <summary>AWS region system name (e.g. "us-east-1"). Empty uses the SDK's own resolution.</summary>
    public string Region { get; init; } = "";
}
