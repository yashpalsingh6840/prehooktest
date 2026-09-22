namespace Package.Mapping;

public sealed partial class StagingCustomersTransform
{
    // ssisx-fill: GapId=SCRIPT-COLUMN:Package:StagingCustomers.SCR_CleanseCustomerRow Author=Copilot Date=2026-09-22 EvidenceSha256=65ea553cecb73be073ab978403716018f50f5590a3f6965e28f9dc8cfad61102
    private partial SCR_CleanseCustomerRowResult SCR_CleanseCustomerRow(Package.Csv.StagingCustomersCsvRow row, in Etl.Core.Abstractions.RowContext ctx)
    {
        var first = (row.FirstName ?? string.Empty).Trim();
        var last = (row.LastName ?? string.Empty).Trim();
        var email = (row.Email ?? string.Empty).Trim();
        var customerId = (row.CustomerID ?? string.Empty).Trim();
        var fullName = (first + " " + last).Trim();
        var cleanEmail = email.ToLowerInvariant();
        var validId = int.TryParse(customerId, out _);
        var validEmail = email.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

        return new(
            FullName: fullName,
            CleanEmail: cleanEmail,
            IsValidRow: validId && validEmail,
            LoadDateTime: DateTime.UtcNow);
    }
}
