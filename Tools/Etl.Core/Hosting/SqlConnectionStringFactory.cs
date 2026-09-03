using Microsoft.Data.SqlClient;

namespace Etl.Core.Hosting;

/// <summary>Builds via SqlConnectionStringBuilder, never string concatenation -- correct escaping
/// of a password containing ';' or '"' is not something to get wrong by hand.</summary>
public static class SqlConnectionStringFactory
{
    public static string Build(DatabaseOptions options)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = options.Server,
            InitialCatalog = options.Database,
            Encrypt = options.Encrypt,
            TrustServerCertificate = options.TrustServerCertificate,
            ConnectTimeout = options.ConnectTimeoutSeconds,
        };

        if (!string.IsNullOrEmpty(options.ApplicationName))
            builder.ApplicationName = options.ApplicationName;

        switch (options.AuthMode)
        {
            case SqlAuthMode.Windows:
                builder.IntegratedSecurity = true;
                break;

            case SqlAuthMode.SqlServer:
                if (string.IsNullOrEmpty(options.UserId))
                    throw new InvalidOperationException("TargetDatabase:UserId is required when AuthMode is SqlServer.");
                if (string.IsNullOrEmpty(options.Password))
                    throw new InvalidOperationException(
                        "TargetDatabase:Password is required when AuthMode is SqlServer. " +
                        "Set it via 'dotnet user-secrets set \"TargetDatabase:Password\" ...' (dev) " +
                        "or the TARGETDATABASE__PASSWORD environment variable (prod) -- never in appsettings.json.");
                builder.UserID = options.UserId;
                builder.Password = options.Password;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.AuthMode, "Unknown SqlAuthMode.");
        }

        return builder.ConnectionString;
    }
}
