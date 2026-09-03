using System.Text.RegularExpressions;
using Etl.Core.Abstractions;
using Package.Csv;

namespace Package.Mapping;

public sealed partial class StagingCustomersTransform
{
    private static readonly Regex EmailPattern =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    // ssisx-fill: GapId=SCRIPT-COLUMN:Package:StagingCustomers.FullName Author=GitHub Copilot Date=2026-09-03 EvidenceSha256=4337f12ee384500ad9cdb5d25d2e59f24e783a67852e5930616a5fce32f5c4a7
    private partial string Fill_FullName(StagingCustomersCsvRow row, in RowContext ctx) =>
        (row.FirstName.Trim() + " " + row.LastName.Trim()).Trim();

    // ssisx-fill: GapId=SCRIPT-COLUMN:Package:StagingCustomers.CleanEmail Author=GitHub Copilot Date=2026-09-03 EvidenceSha256=4337f12ee384500ad9cdb5d25d2e59f24e783a67852e5930616a5fce32f5c4a7
    private partial string Fill_CleanEmail(StagingCustomersCsvRow row, in RowContext ctx) =>
        row.Email.Trim().ToLowerInvariant();

    // ssisx-fill: GapId=SCRIPT-COLUMN:Package:StagingCustomers.IsValidRow Author=GitHub Copilot Date=2026-09-03 EvidenceSha256=4337f12ee384500ad9cdb5d25d2e59f24e783a67852e5930616a5fce32f5c4a7
    private partial bool Fill_IsValidRow(StagingCustomersCsvRow row, in RowContext ctx)
    {
        var email = row.Email.Trim();
        var validId = int.TryParse(row.CustomerID.Trim(), out _);
        var validEmail = email.Length > 0 && EmailPattern.IsMatch(email);
        return validId && validEmail;
    }

    // ssisx-fill: GapId=SCRIPT-COLUMN:Package:StagingCustomers.LoadDateTime Author=GitHub Copilot Date=2026-09-03 EvidenceSha256=4337f12ee384500ad9cdb5d25d2e59f24e783a67852e5930616a5fce32f5c4a7
    private partial DateTime Fill_LoadDateTime(StagingCustomersCsvRow row, in RowContext ctx) =>
        ctx.LoadedAtUtc;
}
