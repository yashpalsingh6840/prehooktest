using System;
using System.Data;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using Microsoft.SqlServer.Dts.Runtime.Wrapper;

[Microsoft.SqlServer.Dts.Pipeline.SSISScriptComponentEntryPointAttribute]
public class ScriptMain : UserComponent
{
    // Row-level Script Component used inside DFT - Build Sales Summary,
    // between the OLE DB Source (joins Orders + OrderDetails + StagingCustomers
    // and aggregates per customer) and the OLE DB Destination into
    // dbo.SalesSummary. Derives a CustomerTier per row from TotalRevenue.
    public override void Input0_ProcessInputRow(Input0Buffer Row)
    {
        double totalRevenue = Row.TotalRevenue_IsNull ? 0.0 : Row.TotalRevenue;

        string tier;
        if (totalRevenue >= 10000)
            tier = "Platinum";
        else if (totalRevenue >= 5000)
            tier = "Gold";
        else if (totalRevenue >= 1000)
            tier = "Silver";
        else
            tier = "Bronze";

        Row.CustomerTier = tier;
        Row.ProcessedDateTime = DateTime.Now;
    }
}
