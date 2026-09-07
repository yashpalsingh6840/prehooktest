using System.Reflection;
using Etl.Core.Abstractions;
using Etl.Core.Csv;
using Etl.Core.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Etl.Core.Hosting;

/// <summary>
/// One-call bootstrap every package console app uses. Host.CreateApplicationBuilder already
/// layers appsettings.json -> appsettings.{Environment}.json -> environment variables ->
/// command line args, and wires console logging -- this adds the shared config layer, User
/// Secrets, the option sections that replace SSIS parameters, and the UnitOfWork every package
/// needs.
/// </summary>
/// <remarks>
/// Takes <paramref name="packageName"/> explicitly rather than the parameterless shape an
/// earlier version had. Two reasons: it gives every package a single source of truth for its own
/// name (resolved from DI as <see cref="PackageIdentity"/>, instead of a second string literal
/// typed again in the generated <c>Program.cs</c>'s own flat script), and it keeps this method
/// from being the place every optional feature gets unconditionally wired in -- e.g. email notifications are NOT
/// registered here; a package opts in explicitly via
/// <c>builder.Services.AddEmailNotifications(builder.Configuration)</c> in its own Program.cs.
/// </remarks>
public static class EtlHost
{
    public static HostApplicationBuilder Create(string[] args, string packageName)
    {
        CodePageSupport.EnsureRegistered();

        var builder = Host.CreateApplicationBuilder(args);

        // Lowest-precedence layer: settings shared by every package (SMTP host, default
        // BulkCopy tuning, the target SQL Server/database). Inserted at index 0 so the
        // per-package appsettings.json/appsettings.{Environment}.json/env vars/command line
        // Host.CreateApplicationBuilder already added all outrank it for any key both define.
        builder.Configuration.Sources.Insert(0, new JsonConfigurationSource
        {
            Path = "appsettings.Shared.json",
            Optional = false,
            ReloadOnChange = false,
        });

        var entryAssembly = Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException("Could not resolve the entry assembly for User Secrets.");
        builder.Configuration.AddUserSecrets(entryAssembly, optional: true);

        builder.Services.AddSingleton(new PackageIdentity(packageName));

        builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("TargetDatabase"));
        builder.Services.Configure<FileSourceOptions>(builder.Configuration.GetSection("FileSource"));
        builder.Services.Configure<BulkCopyOptions>(builder.Configuration.GetSection("BulkCopy"));

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ISqlBulkCopyFactory, SqlBulkCopyFactory>();
        builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

        return builder;
    }
}
