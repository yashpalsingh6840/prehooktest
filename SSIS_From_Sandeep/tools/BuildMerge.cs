// Adds a FOURTH data flow to Package_Transforms.dtsx: the Merge transform.
//
//   Flat File -> Data Conversion -> Conditional Split -+-> Sort -+
//                                                       |         +-> Merge -> OLE DB Dest
//                                                       +-> Sort -+
//
// Merge is NOT Merge Join. Merge Join combines two DIFFERENT row shapes on a key;
// Merge concatenates two inputs of the SAME shape while preserving sort order. That is
// the "Lookup/Merge" pattern in production's ETAccSSIS.
//
// Splitting one source in two and merging it back gives two inputs guaranteed to have
// identical metadata, which Merge requires.
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.SqlServer.Dts.Runtime;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using RTWrap = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisBuild
{
    public static class MergeFlow
    {
        static StringBuilder _log;
        static void Log(string s) { _log.AppendLine(s); }
        static void Step(Action a, string what)
        {
            try { a(); Log("  .. " + what + " OK"); }
            catch { Log("  .. " + what + " <-- FAILED HERE"); throw; }
        }

        public static string Add(string pkgPath, string csvPath, string oleDbConn)
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
            if (pkg.Executables.Contains("DFT_MergeSortedBranches"))
            { Log("DFT_MergeSortedBranches already present"); return _log.ToString(); }

            var cmOle = pkg.Connections["CM_SQL_SSISDemo"];
            var cmFF = Ssis.FlatFile(pkg, "CM_FF_Customers4", csvPath, true, "Delimited");
            foreach (var c in new[] { "CustomerID", "FirstName", "LastName", "Email", "Phone", "SignupDate" })
                Ssis.FlatFileColumn(cmFF, c, ",", 0, 200);
            Ssis.FlatFileColumn(cmFF, "Country", "\r\n", 0, 200);

            var trunc = Ssis.ExecSql(pkg, "SQL_TruncateMerged", cmOle,
                                     "TRUNCATE TABLE dbo.CustomerMerged;");
            var dft = Ssis.DataFlow(pkg, "DFT_MergeSortedBranches");
            Ssis.Link(pkg, trunc, dft, DTSExecResult.Success);
            Ssis.Link(pkg, pkg.Executables["DFT_SortAndMergeJoin"], trunc, DTSExecResult.Success);
            MainPipe pipe = Ssis.Pipe(dft);

            var src = Ssis.Comp(pipe, "FF_SRC_Customers4", "Flat File Source");
            Ssis.SetConn(src, pkg, cmFF);
            var sw = src.Instantiate();
            sw.AcquireConnections(null); sw.ReinitializeMetaData(); sw.ReleaseConnections();

            // CustomerID -> DT_I4 so it can be a numeric sort key
            var conv = Ssis.Comp(pipe, "DCONV_MergeId", "Data Conversion");
            Ssis.Connect(pipe, src, 0, conv, 0);
            var cw = conv.Instantiate();
            var cin = conv.InputCollection[0];
            var cv = cin.GetVirtualInput();
            int lid = 0;
            foreach (IDTSVirtualInputColumn100 vc in cv.VirtualInputColumnCollection)
                if (vc.Name == "CustomerID") { lid = vc.LineageID; break; }
            cw.SetUsageType(cin.ID, cv, lid, DTSUsageType.UT_READONLY);
            int coid = conv.OutputCollection[0].ID;
            var oc = cw.InsertOutputColumnAt(coid, 0, "CustomerID_i4", "");
            cw.SetOutputColumnDataTypeProperties(coid, oc.ID, RTWrap.DataType.DT_I4, 0, 0, 0, 0);
            cw.SetOutputColumnProperty(coid, oc.ID, "SourceInputColumnLineageID", lid);
            oc.ErrorRowDisposition = DTSRowDisposition.RD_IgnoreFailure;
            oc.TruncationRowDisposition = DTSRowDisposition.RD_IgnoreFailure;

            // split into two same-shaped streams
            var split = Ssis.Comp(pipe, "CSPLIT_ByCountry", "Conditional Split");
            Ssis.Connect(pipe, conv, 0, split, 0);
            var spW = split.Instantiate();
            var sin = split.InputCollection[0];
            var sv = sin.GetVirtualInput();
            int cLid = 0;
            foreach (IDTSVirtualInputColumn100 vc in sv.VirtualInputColumnCollection)
                if (vc.Name == "Country") { cLid = vc.LineageID; break; }
            spW.SetUsageType(sin.ID, sv, cLid, DTSUsageType.UT_READONLY);
            var br = spW.InsertOutput(DTSInsertPlacement.IP_BEFORE, split.OutputCollection[0].ID);
            br.Name = "Canada";
            spW.SetOutputProperty(br.ID, "Expression", "TRIM([Country]) == \"Canada\"");
            spW.SetOutputProperty(br.ID, "FriendlyExpression", "TRIM([Country]) == \"Canada\"");
            split.OutputCollection[1].Name = "RestOfWorld";
            Log("Conditional Split   : Canada / RestOfWorld (two identical shapes)");

            var sortA = AddSort(pipe, split, 0, "SORT_Canada");
            var sortB = AddSort(pipe, split, 1, "SORT_RestOfWorld");
            Log("Sort x2             : both branches sorted on CustomerID_i4");

            // ---- Merge --------------------------------------------------------------
            var merge = Ssis.Comp(pipe, "MRG_SortedUnion", "Merge");
            Step(() => Ssis.Connect(pipe, sortA, 0, merge, 0), "attach branch A -> Merge");
            Step(() => Ssis.Connect(pipe, sortB, 0, merge, 1), "attach branch B -> Merge");
            var produced = new List<string>();
            foreach (IDTSOutputColumn100 o in merge.OutputCollection[0].OutputColumnCollection)
                produced.Add(o.Name);
            Log("Merge               : " + merge.InputCollection.Count + " inputs, " +
                produced.Count + " output columns");

            var dst = Ssis.Comp(pipe, "OLEDST_CustomerMerged", "OLE DB Destination");
            Ssis.SetConn(dst, pkg, cmOle);
            dst.CustomPropertyCollection["AccessMode"].Value = 3;
            dst.CustomPropertyCollection["OpenRowset"].Value = "[dbo].[CustomerMerged]";
            Ssis.Connect(pipe, merge, 0, dst, 0);
            var dw = dst.Instantiate();
            dw.AcquireConnections(null); dw.ReinitializeMetaData(); dw.ReleaseConnections();
            int m = Ssis.MapDestination(dst, 0,
                        new[] { "CustomerID_i4", "Email" },
                        new[] { "CustomerID",    "Email" });
            Log("OLE DB Destination  : dbo.CustomerMerged, " + m + " columns");

            app.SaveToXml(pkgPath, pkg, null);
            Log("");
            Log("saved " + pkgPath);
            return _log.ToString();
        }

        static IDTSComponentMetaData100 AddSort(MainPipe pipe, IDTSComponentMetaData100 upstream,
                                                int outputIndex, string name)
        {
            var sort = Ssis.Comp(pipe, name, "Sort");
            var path = pipe.PathCollection.New();
            path.AttachPathAndPropagateNotifications(upstream.OutputCollection[outputIndex],
                                                     sort.InputCollection[0]);
            var w = sort.Instantiate();
            var input = sort.InputCollection[0];
            var v = input.GetVirtualInput();
            var lids = new List<int>();
            foreach (IDTSVirtualInputColumn100 vc in v.VirtualInputColumnCollection)
                lids.Add(vc.LineageID);
            foreach (int l in lids) w.SetUsageType(input.ID, v, l, DTSUsageType.UT_READONLY);
            foreach (IDTSInputColumn100 ic in input.InputColumnCollection)
                w.SetInputColumnProperty(input.ID, ic.ID, "NewSortKeyPosition",
                                         ic.Name == "CustomerID_i4" ? 1 : 0);
            return sort;
        }
    }
}
