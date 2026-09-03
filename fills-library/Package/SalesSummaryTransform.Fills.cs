using Etl.Core.Abstractions;
using Package.Sql;

namespace Package.Mapping;

public sealed partial class SalesSummaryTransform
{
    // ssisx-fill: GapId=SCRIPT-COLUMN:Package:SalesSummary.CustomerTier Author=GitHub Copilot Date=2026-09-03 EvidenceSha256=e886e4502d877a2be1c82def13f4ecedc4c9005e89bde562a5ffce1cc4441ef0
    private partial string Fill_CustomerTier(SalesSummarySqlRow row, in RowContext ctx)
    {
        var revenue = row.TotalRevenue;
        if (revenue >= 10000) return "Platinum";
        if (revenue >= 5000) return "Gold";
        if (revenue >= 1000) return "Silver";
        return "Bronze";
    }

    // ssisx-fill: GapId=SCRIPT-COLUMN:Package:SalesSummary.ProcessedDateTime Author=GitHub Copilot Date=2026-09-03 EvidenceSha256=e886e4502d877a2be1c82def13f4ecedc4c9005e89bde562a5ffce1cc4441ef0
    private partial DateTime Fill_ProcessedDateTime(SalesSummarySqlRow row, in RowContext ctx) =>
        ctx.LoadedAtUtc;
}
