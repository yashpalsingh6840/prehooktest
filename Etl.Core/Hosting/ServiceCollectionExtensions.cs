using Etl.Core.Abstractions;
using Etl.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Etl.Core.Hosting;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a package's DbContext, reading its connection string from DatabaseOptions, and
    /// aliases it as the plain DbContext that IUnitOfWork depends on -- so the UnitOfWork resolves
    /// to the SAME scoped instance, not a second unrelated context.
    /// </summary>
    public static IServiceCollection AddEtlDbContext<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.AddDbContext<TContext>((sp, optionsBuilder) =>
        {
            var dbOptions = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            optionsBuilder.UseSqlServer(SqlConnectionStringFactory.Build(dbOptions));
        });

        services.AddScoped<DbContext>(sp => sp.GetRequiredService<TContext>());

        return services;
    }

    public static IServiceCollection AddBulkSink<TEntity>(this IServiceCollection services)
        where TEntity : class
    {
        services.AddScoped<IBulkSink<TEntity>, SqlBulkSink<TEntity>>();
        return services;
    }
}
