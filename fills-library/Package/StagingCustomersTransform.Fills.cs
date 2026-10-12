using System;
using System.Text.RegularExpressions;
using Etl.Core.Abstractions;
using Package.Csv;
using Package.Model;

namespace Package.Mapping;

public sealed partial class StagingCustomersTransform
{
    private static readonly Regex EmailPattern =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    // ssisx-fill: GapId=SCRIPT-COLUMN:Package:StagingCustomers.SCR_CleanseCustomerRow Author=GitHub Copilot Date=2026-10-09 EvidenceSha256=65ea553cecb73be073ab978403716018f50f5590a3f6965e28f9dc8cfad61102
    private partial SCR_CleanseCustomerRowResult SCR_CleanseCustomerRow(StagingCustomersCsvRow row, in RowContext ctx)
    {
        var firstName = (row.FirstName ?? string.Empty).Trim();
        var lastName = (row.LastName ?? string.Empty).Trim();
        var email = (row.Email ?? string.Empty).Trim();
        var customerId = (row.CustomerID ?? string.Empty).Trim();
        var fullName = (firstName + " " + lastName).Trim();
        var cleanEmail = email.ToLowerInvariant();
        var validId = int.TryParse(customerId, out _);
        var validEmail = email.Length > 0 && EmailPattern.IsMatch(email);

        return new SCR_CleanseCustomerRowResult(
            FullName: fullName,
            CleanEmail: cleanEmail,
            IsValidRow: validId && validEmail,
            LoadDateTime: DateTime.UtcNow);
    }
}