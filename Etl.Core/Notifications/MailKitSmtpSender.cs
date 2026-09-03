using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Etl.Core.Notifications;

public sealed class MailKitSmtpSender : ISmtpSender
{
    public async Task SendAsync(MimeMessage message, NotificationOptions options, CancellationToken ct)
    {
        using var client = new SmtpClient();

        var socketOptions = options.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(options.SmtpHost, options.SmtpPort, socketOptions, ct);

        if (!string.IsNullOrEmpty(options.Username))
            await client.AuthenticateAsync(options.Username, options.Password ?? string.Empty, ct);

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}
