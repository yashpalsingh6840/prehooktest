using MimeKit;

namespace Etl.Core.Notifications;

/// <summary>The one MailKit call EmailPackageResultNotifier makes, pulled out to its own
/// interface so a test can supply a fake instead of connecting to a real SMTP server.</summary>
public interface ISmtpSender
{
    Task SendAsync(MimeMessage message, NotificationOptions options, CancellationToken ct);
}
