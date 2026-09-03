using System.Text;
using Etl.Core.Abstractions;
using Etl.Core.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Etl.Core.Notifications;

/// <summary>
/// Emails a run's outcome -- on success AND on failure, per package, since a load that silently
/// stops running is as much an operational problem as one that fails loudly. Recipients are
/// chosen by outcome (OnSuccessRecipients vs OnFailureRecipients); an empty list for that
/// outcome just means "nobody asked to hear about this," not an error.
/// </summary>
public sealed class EmailPackageResultNotifier(
    IOptions<NotificationOptions> options,
    ISmtpSender sender,
    PackageIdentity identity,
    ILogger<EmailPackageResultNotifier> logger) : IPackageResultNotifier
{
    public async Task NotifyAsync(PackageResult result, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            logger.LogDebug("Notifications disabled; skipping email for {Package}", identity.Name);
            return;
        }

        var recipients = result.Succeeded ? o.OnSuccessRecipients : o.OnFailureRecipients;
        if (recipients.Count == 0)
        {
            logger.LogDebug("No recipients configured for a {Outcome} of {Package}; skipping email",
                result.Succeeded ? "success" : "failure", identity.Name);
            return;
        }

        var message = BuildMessage(result, o, recipients);

        try
        {
            await sender.SendAsync(message, o, ct);
            logger.LogInformation("Sent {Outcome} notification for {Package} to {Count} recipient(s)",
                result.Succeeded ? "success" : "failure", identity.Name, recipients.Count);
        }
        catch (Exception ex)
        {
            // A notification failure must never change the package's own exit code -- the load
            // already succeeded or failed on its own merits before this method was ever called.
            logger.LogError(ex, "Failed to send {Outcome} notification email for {Package}",
                result.Succeeded ? "success" : "failure", identity.Name);
        }
    }

    private static MimeMessage BuildMessage(PackageResult result, NotificationOptions o, List<string> recipients)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        foreach (var recipient in recipients)
            message.To.Add(MailboxAddress.Parse(recipient));

        message.Subject = $"[{(result.Succeeded ? "SUCCESS" : "FAILURE")}] {result.Name} ({result.Elapsed.TotalSeconds:N1}s)";
        message.Body = new TextPart("plain") { Text = BuildBody(result) };
        return message;
    }

    private static string BuildBody(PackageResult result)
    {
        var body = new StringBuilder();
        body.AppendLine($"Package:  {result.Name}");
        body.AppendLine($"Outcome:  {(result.Succeeded ? "Succeeded" : "Failed")}");
        body.AppendLine($"Elapsed:  {result.Elapsed}");
        body.AppendLine();

        foreach (var step in result.Steps)
            body.AppendLine($"  {step.Name}: {step.RowsRead:N0} read, {step.RowsWritten:N0} written ({step.Elapsed})");

        if (result.Error is not null)
        {
            body.AppendLine();
            body.AppendLine("Error:");
            body.AppendLine(result.Error.ToString());
        }

        return body.ToString();
    }
}
