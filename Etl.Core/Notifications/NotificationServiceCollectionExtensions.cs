using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Etl.Core.Notifications;

public static class NotificationServiceCollectionExtensions
{
    /// <summary>
    /// Opt-in, not automatic: a package's own Program.cs calls this explicitly, rather than
    /// EtlHost.Create wiring it into every package unconditionally (see EtlHost's remarks on why
    /// that unconditional-wiring shape was a problem in the first place).
    /// </summary>
    public static IServiceCollection AddEmailNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NotificationOptions>(configuration.GetSection("Notification"));
        services.AddSingleton<ISmtpSender, MailKitSmtpSender>();
        services.AddScoped<IPackageResultNotifier, EmailPackageResultNotifier>();
        return services;
    }
}
