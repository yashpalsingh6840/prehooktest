// Adds a SECOND data flow to Package_Transforms.dtsx covering the remaining stock
// components: Lookup, Multicast, Aggregate and Row Count.
//
//   Flat File Source -> Lookup (Country -> CountryName/Code/Region)
//        match    -> Multicast -+-> Aggregate (group by Region, count) -> OLE DB Dest
//                               +-> Row Count (-> User::MatchedRows)
//        no match -> Row Count  (-> User::UnmatchedRows)
//
// CountryLookup deliberately omits 'Czech Republic' and 'Spain', so the no-match
// output really fires instead of only the happy path being exercised.
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.SqlServer.Dts.Runtime;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using RTWrap = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisBuild
{
    public static class TransformsPackage2
    {
        static StringBuilder _log;
        static void Log(string s) { _log.AppendLine(s); }

        /// Runs one configuration call, logging which step it was before rethrowing -
        /// bare pipeline HRESULTs give no indication of the failing call.
        static void Step(Action a, string what)
        {
            try { a(); Log("  .. " + what + " OK"); }
            catch { Log("  .. " + what + " <-- FAILED HERE"); throw; }
        }

        public static string AddLookupFlow(string pkgPath, string csvPath, string oleDbConn)
        {
            _log = new StringBuilder();
            try { return Core(pkgPath, csvPath, oleDbConn); }
            catch (Exception ex)
            {
                _log.AppendLine();
                _log.AppendLine("*** FAILED after the steps above ***");
                _log.AppendLine(ex.GetType().Name + ": " + ex.Message);
                return _log.ToString();
            }
        }

        static string Core(string pkgPath, string csvPath, string oleDbConn)
        {
            var app = new Application();
            var pkg = app.LoadPackage(pkgPath, null);

            if (pkg.Executables.Contains("DFT_LookupAndAggregate"))
            { Log("DFT_LookupAndAggregate already present - nothing to do"); return _log.ToString(); }

            var cmOle = pkg.Connections["CM_SQL_SSISDemo"];

            // a second flat file CM: the Lookup flow reads the same CSV independently
            var cmFF2 = Ssis.FlatFile(pkg, "CM_FF_Customers2", csvPath, true, "Delimited");
            foreach (var c in new[] { "CustomerID", "FirstName", "LastName", "Email", "Phone", "SignupDate" })
                Ssis.FlatFileColumn(cmFF2, c, ",", 0, 200);
            Ssis.FlatFileColumn(cmFF2, "Country", "\r\n", 0, 200);

            pkg.Variables.Add("MatchedRows", false, "User", 0);
            pkg.Variables.Add("UnmatchedRows", false, "User", 0);

            var trunc = Ssis.ExecSql(pkg, "SQL_TruncateRegionSummary", cmOle,
                                     "TRUNCATE TABLE dbo.RegionSummary;");
            var dft = Ssis.DataFlow(pkg, "DFT_LookupAndAggregate");
            Ssis.Link(pkg, trunc, dft, DTSExecResult.Success);
            // run after the first flow so the package reads top-to-bottom
            Ssis.Link(pkg, pkg.Executables["DFT_DerivedAndSplit"], trunc, DTSExecResult.Success);

            MainPipe pipe = Ssis.Pipe(dft);

            // --- source ---------------------------------------------------------
            var src = Ssis.Comp(pipe, "FF_SRC_Customers2", "Flat File Source");
            Ssis.SetConn(src, pkg, cmFF2);
            var srcW = src.Instantiate();
            srcW.AcquireConnections(null); srcW.ReinitializeMetaData(); srcW.ReleaseConnections();
            Log("Flat File Source    : " + src.OutputCollection[0].OutputColumnCollection.Count + " columns");

            // --- Lookup ----------------------------------------------------------
            var lkp = Ssis.Comp(pipe, "LKP_Country", "Lookup");
            Log("  .. Lookup component created");
            Ssis.Connect(pipe, src, 0, lkp, 0);
            Log("  .. Lookup input attached");
            var lkpW = lkp.Instantiate();
            Log("  .. Lookup wrapper obtained");
            // Attach the connection FIRST - SqlCommand cannot validate without one.
            Log("  .. cmOle id=" + cmOle.ID + " name=" + cmOle.Name +
                " runtimeConns=" + lkp.RuntimeConnectionCollection.Count);
            lkp.RuntimeConnectionCollection[0].ConnectionManagerID = cmOle.ID;
            Log("  .. ConnectionManagerID set");
            lkp.RuntimeConnectionCollection[0].ConnectionManager =
                DtsConvert.GetExtendedInterface(cmOle);
            Log("  .. ConnectionManager set");
            // NoMatchBehavior 1 = send unmatched rows to the no-match output
            // (0 would fail the component on the first miss).
            Step(() => lkpW.SetComponentProperty("NoMatchBehavior", 1), "NoMatchBehavior");
            Step(() => lkpW.SetComponentProperty("CacheType", 0), "CacheType");
            Step(() => lkpW.SetComponentProperty("SqlCommand",
                "SELECT CountryName, CountryCode, Region FROM dbo.CountryLookup"), "SqlCommand");
            Step(() => lkpW.AcquireConnections(null), "AcquireConnections");
            Step(() => lkpW.ReinitializeMetaData(), "ReinitializeMetaData");

            // join CSV [Country] -> reference [CountryName]
            var lin = lkp.InputCollection[0];
            var lv = lin.GetVirtualInput();
            int countryLid = 0;
            var seen = new List<string>();
            foreach (IDTSVirtualInputColumn100 vc in lv.VirtualInputColumnCollection)
            {
                seen.Add(vc.Name);
                if (vc.Name == "Country") countryLid = vc.LineageID;
            }
            Log("  .. virtual input columns: " + string.Join(", ", seen.ToArray()));
            Log("  .. Country lineageID = " + countryLid);
            if (countryLid == 0) throw new Exception("input column 'Country' not found on the Lookup");
            Step(() => lkpW.SetUsageType(lin.ID, lv, countryLid, DTSUsageType.UT_READONLY),
                 "SetUsageType(Country)");
            foreach (IDTSInputColumn100 ic in lin.InputColumnCollection)
            {
                if (ic.Name != "Country") continue;
                var props = new List<string>();
                foreach (IDTSCustomProperty100 p in ic.CustomPropertyCollection) props.Add(p.Name);
                Log("  .. input column custom props: " + string.Join(", ", props.ToArray()));
                var col = ic;
                Step(() => lkpW.SetInputColumnProperty(lin.ID, col.ID,
                        "JoinToReferenceColumn", "CountryName"), "JoinToReferenceColumn");
            }

            // pull two reference columns onto the match output
            Step(() => AddLookupOutputColumn(lkpW, lkp, "CountryCode", 10), "outCol CountryCode");
            Step(() => AddLookupOutputColumn(lkpW, lkp, "Region", 50), "outCol Region");
            lkpW.ReleaseConnections();
            Log("Lookup              : Country -> CountryCode, Region (no-match redirected)");

            // --- Multicast on the MATCH output ------------------------------------
            var mcast = Ssis.Comp(pipe, "MCAST_Matched", "Multicast");
            Ssis.Connect(pipe, lkp, 0, mcast, 0);        // output 0 = match
            Log("Multicast           : fan-out from the match output");

            // --- Aggregate: count customers per Region ----------------------------
            var agg = Ssis.Comp(pipe, "AGG_ByRegion", "Aggregate");
            Log("  .. mcast outputs before agg attach = " + mcast.OutputCollection.Count);
            var pAgg = pipe.PathCollection.New();
            Step(() => pAgg.AttachPathAndPropagateNotifications(
                     mcast.OutputCollection[0], agg.InputCollection[0]), "attach mcast->agg");
            Log("  .. mcast outputs after  agg attach = " + mcast.OutputCollection.Count);
            var aggW = agg.Instantiate();
            var aIn = agg.InputCollection[0];
            var av = aIn.GetVirtualInput();
            int regionLid = 0;
            foreach (IDTSVirtualInputColumn100 vc in av.VirtualInputColumnCollection)
                if (vc.Name == "Region") { regionLid = vc.LineageID; break; }
            int custLid = 0;
            foreach (IDTSVirtualInputColumn100 vc in av.VirtualInputColumnCollection)
                if (vc.Name == "CustomerID") { custLid = vc.LineageID; break; }
            Log("  .. Region lineageID = " + regionLid + ", CustomerID lineageID = " + custLid);
            if (regionLid == 0 || custLid == 0)
                throw new Exception("Region/CustomerID not visible on the Aggregate input");
            // Two columns selected: Region is the group key, CustomerID is what gets
            // counted. A count needs a real column to point at - AggregationColumnId is
            // validated even for "count all", so counting an actual column is simpler.
            Step(() => aggW.SetUsageType(aIn.ID, av, regionLid, DTSUsageType.UT_READONLY),
                 "agg SetUsageType(Region)");
            Step(() => aggW.SetUsageType(aIn.ID, av, custLid, DTSUsageType.UT_READONLY),
                 "agg SetUsageType(CustomerID)");

            // NOTE: Aggregate carries no AggregationType on the INPUT column - that
            // property exists only on output columns. Setting it on the input fails
            // with 0xC0204006. The input just has to be selected.
            int ricLineage = 0, cicLineage = 0;
            foreach (IDTSInputColumn100 ic in aIn.InputColumnCollection)
            {
                if (ic.Name == "Region")     ricLineage = ic.LineageID;
                if (ic.Name == "CustomerID") cicLineage = ic.LineageID;
            }
            var rLin = ricLineage; var cLin = cicLineage;
            Log("  .. agg input lineages: Region=" + rLin + " CustomerID=" + cLin);

            var aggOut = agg.OutputCollection[0];
            // group-by key on the output. AggregationColumnId binds it to the input
            // column and must be set BEFORE the data type, same rule as the Lookup.
            var ocRegion = aggW.InsertOutputColumnAt(aggOut.ID, 0, "Region", "");
            Step(() => aggW.SetOutputColumnProperty(aggOut.ID, ocRegion.ID, "AggregationType", 0),
                 "agg out Region AggregationType");
            Step(() => aggW.SetOutputColumnProperty(aggOut.ID, ocRegion.ID,
                     "AggregationColumnId", rLin), "agg out Region AggregationColumnId");
            // No explicit data type for the group-by column: binding it through
            // AggregationColumnId already derives the type from the input column, and
            // setting it afterwards fails with 0xC020401A (same rule as the Lookup).
            // count(*) DOES need one - it is a brand-new column, bound to nothing.
            var ocCount = aggW.InsertOutputColumnAt(aggOut.ID, 1, "CustomerCount", "");
            // AggregationType first - it is what defines the column. "count all" yields
            // DT_I4 by itself, so no explicit data-type call (which fails, 0xC020401A).
            // AggregationType 1 = Count (of a named column). AggregationColumnId must be
            // a REAL input lineage: with "count all" (3) it is still validated and any
            // placeholder is rejected as "The value for custom property
            // AggregationColumnId ... is incorrect".
            Step(() => aggW.SetOutputColumnProperty(aggOut.ID, ocCount.ID, "AggregationType", 1),
                 "agg out Count AggregationType=Count");
            Step(() => aggW.SetOutputColumnProperty(aggOut.ID, ocCount.ID, "AggregationColumnId", cLin),
                 "agg out Count AggregationColumnId=CustomerID");
            Log("  .. count column type = " + ocCount.DataType);
            Log("Aggregate           : group by Region, COUNT(*)");

            var destAgg = Ssis.Comp(pipe, "OLEDST_RegionSummary", "OLE DB Destination");
            Ssis.SetConn(destAgg, pkg, cmOle);
            destAgg.CustomPropertyCollection["AccessMode"].Value = 3;
            destAgg.CustomPropertyCollection["OpenRowset"].Value = "[dbo].[RegionSummary]";
            Ssis.Connect(pipe, agg, 0, destAgg, 0);
            var daW = destAgg.Instantiate();
            daW.AcquireConnections(null); daW.ReinitializeMetaData(); daW.ReleaseConnections();
            int m = Ssis.MapDestination(destAgg, 0,
                        new[] { "Region", "CustomerCount" },
                        new[] { "Region", "CustomerCount" });
            Log("OLE DB Destination  : dbo.RegionSummary, " + m + " columns");

            // --- Row Count on the Multicast's second branch ------------------------
            var rcMatched = Ssis.Comp(pipe, "RC_MatchedRows", "Row Count");
            var pRc = pipe.PathCollection.New();
            pRc.AttachPathAndPropagateNotifications(mcast.OutputCollection[1], rcMatched.InputCollection[0]);
            rcMatched.Instantiate().SetComponentProperty("VariableName", "User::MatchedRows");

            // --- Row Count on the Lookup's NO-MATCH output -------------------------
            var rcNo = Ssis.Comp(pipe, "RC_UnmatchedRows", "Row Count");
            var pNo = pipe.PathCollection.New();
            pNo.AttachPathAndPropagateNotifications(lkp.OutputCollection[1], rcNo.InputCollection[0]);
            rcNo.Instantiate().SetComponentProperty("VariableName", "User::UnmatchedRows");
            Log("Row Count x2        : User::MatchedRows, User::UnmatchedRows");

            app.SaveToXml(pkgPath, pkg, null);
            Log("");
            Log("saved " + pkgPath);
            return _log.ToString();
        }

        /// Copies a reference-table column onto the Lookup's match output.
        ///
        /// CopyFromReferenceColumn must be set BEFORE any data-type call: binding to the
        /// reference column is what gives the output column its type. Setting the type
        /// first fails with 0xC020401A, and once bound the type is already correct, so
        /// no explicit SetOutputColumnDataTypeProperties is needed at all.
        static void AddLookupOutputColumn(CManagedComponentWrapper w,
                                          IDTSComponentMetaData100 lkp, string refCol, int len)
        {
            var outp = lkp.OutputCollection[0];      // 0 = match output
            var oc = w.InsertOutputColumnAt(outp.ID, outp.OutputColumnCollection.Count, refCol, "");
            w.SetOutputColumnProperty(outp.ID, oc.ID, "CopyFromReferenceColumn", refCol);
        }
    }
}
