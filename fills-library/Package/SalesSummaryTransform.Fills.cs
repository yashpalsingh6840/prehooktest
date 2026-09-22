namespace Package.Mapping;

public sealed partial class SalesSummaryTransform
{
    // ssisx-fill: GapId=SCRIPT-COLUMN:Package:SalesSummary.SCR_ComputeCustomerTier Author=Copilot Date=2026-09-22 EvidenceSha256=acb950341c43d6703f6b632a5deaa307a86c1d04914c3f382b6ea5acc05f95cf
    private partial SCR_ComputeCustomerTierResult SCR_ComputeCustomerTier(Package.Sql.SalesSummarySqlRow row, in Etl.Core.Abstractions.RowContext ctx)
    {
        var customerTier = row.TotalRevenue >= 10000
            ? "Platinum"
            : row.TotalRevenue >= 5000
                ? "Gold"
                : row.TotalRevenue >= 1000
                    ? "Silver"
                    : "Bronze";

        return new(
            CustomerTier: customerTier,
            ProcessedDateTime: DateTime.UtcNow);
    }
}
