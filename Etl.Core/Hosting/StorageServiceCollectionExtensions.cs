using Etl.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Etl.Core.Hosting;

public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Makes this package read and write files in S3 instead of on the local filesystem, per the
    /// "Storage" configuration section. A generated package calls this only when it was generated
    /// with <c>--storage s3</c>; <see cref="EtlHost.Create"/> otherwise registers the local store.
    /// Fails fast at startup if no bucket is configured, rather than at the first file access.
    /// </summary>
    public static IServiceCollection AddS3FileStore(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        if (string.IsNullOrWhiteSpace(options.Bucket))
            throw new InvalidOperationException(
                "This package was generated for S3 storage but Storage:Bucket is not configured. " +
                "Set it in appsettings.json, an environment variable (Storage__Bucket) or on the command line.");

        // Built here, not lazily, so a missing region surfaces at startup with an actionable message
        // instead of as an AWS SDK exception on the first file access.
        S3FileStore store;
        try
        {
            store = S3FileStore.Create(options.Bucket, options.Region);
        }
        catch (Amazon.Runtime.AmazonClientException ex)
        {
            throw new InvalidOperationException(
                "Could not create the S3 client: " + ex.Message + " Set Storage:Region in appsettings.json " +
                "(or the AWS_REGION environment variable / an AWS profile region).", ex);
        }

        services.RemoveAll<IFileStore>();
        services.AddSingleton<IFileStore>(store);
        return services;
    }
}
