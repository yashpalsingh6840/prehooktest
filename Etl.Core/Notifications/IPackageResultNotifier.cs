using Etl.Core.Abstractions;

namespace Etl.Core.Notifications;

/// <summary>Sends a run's outcome somewhere. The only implementation today is email, but
/// Program.cs depends on this interface, not on MailKit, so a package can substitute (or a test
/// can fake) the notification channel without touching the runner.</summary>
public interface IPackageResultNotifier
{
    Task NotifyAsync(PackageResult result, CancellationToken ct);
}
