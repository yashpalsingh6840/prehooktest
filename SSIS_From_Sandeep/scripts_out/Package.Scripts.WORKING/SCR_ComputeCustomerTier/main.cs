using System;
using Microsoft.SqlServer.Dts.Pipeline;

namespace SC_ComputeCustomerTier
{
    /// <summary>
    /// Second row-level Script Component (requirement 4), this one in
    /// DFT_BuildSalesSummary. Assigns a loyalty tier from the aggregated revenue and
    /// stamps the processing time.
    ///
    /// Thresholds match sql\04_verify_expected_output.sql exactly, so the package output
    /// can be diffed against the pure T-SQL harness.
    /// </summary>
    [SSISScriptComponentEntryPoint]
    public class ScriptMain : UserComponent
    {
        public override void Input0_ProcessInputRow(Input0Buffer Row)
        {
            double revenue = Row.TotalRevenue;

            if (revenue >= 10000)     Row.CustomerTier = "Platinum";
            else if (revenue >= 5000) Row.CustomerTier = "Gold";
            else if (revenue >= 1000) Row.CustomerTier = "Silver";
            else                      Row.CustomerTier = "Bronze";

            Row.ProcessedDateTime = DateTime.Now;
        }
    }
}
