namespace Etl.Core.Notifications;

/// <summary>Email notification settings. SmtpHost/Port/credentials are shared across every
/// package (appsettings.Shared.json); OnSuccessRecipients/OnFailureRecipients are NOT -- each
/// package declares its own, in its own appsettings.json.</summary>
public sealed class NotificationOptions
{
    public bool Enabled { get; init; } = true;

    public required string SmtpHost { get; init; }
    public int SmtpPort { get; init; } = 25;
    public bool UseSsl { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }

    public required string FromAddress { get; init; }
    public string FromName { get; init; } = "SSIS Rewrite";

    public List<string> OnSuccessRecipients { get; init; } = [];
    public List<string> OnFailureRecipients { get; init; } = [];
}
