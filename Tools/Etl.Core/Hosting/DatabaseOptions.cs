namespace Etl.Core.Hosting;

public enum SqlAuthMode
{
    /// <summary>Windows Integrated Security -- used by LoadReferenceData.</summary>
    Windows,

    /// <summary>SQL Server Authentication -- used by LoadEmployees (login `ssis_loader`).</summary>
    SqlServer,
}

/// <summary>Replaces SSIS's project/package connection parameters + the SSISDB sensitive parameter.</summary>
public sealed class DatabaseOptions
{
    public required string Server { get; init; }
    public required string Database { get; init; }
    public SqlAuthMode AuthMode { get; init; } = SqlAuthMode.Windows;
    public string? UserId { get; init; }

    /// <summary>Never set this in appsettings.json. User Secrets (dev) or an environment variable (prod) only.</summary>
    public string? Password { get; init; }

    /// <summary>Microsoft.Data.SqlClient defaults Encrypt to true, unlike the SSIS-era MSOLEDBSQL provider.</summary>
    public bool Encrypt { get; init; } = true;

    /// <summary>Required for a local instance with a self-signed certificate.</summary>
    public bool TrustServerCertificate { get; init; }

    public int ConnectTimeoutSeconds { get; init; } = 15;

    public string? ApplicationName { get; init; }
}
