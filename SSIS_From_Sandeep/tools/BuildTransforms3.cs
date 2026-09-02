// Adds a THIRD data flow to Package_Transforms.dtsx: Sort x2 + Merge Join.
//
//   Customers.csv        -> Data Conversion -> Sort (CustomerID) -+
//                                                                 +-> Merge Join (INNER)
//   CustomerContacts.csv -> Data Conversion -> Sort (CustomerID) -+        -> OLE DB Dest
//
// Merge Join demands SORTED inputs. The Sort component sets IsSorted and the
// SortKeyPositions on its output for us, which is why two Sorts sit in front of it.
// CustomerContacts.csv omits customer 8 and adds a non-existent 11, so the inner
// join genuinely drops a row from each side rather than matching everything.
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.SqlServer.Dts.Runtime;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using RTWrap = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisBuild
{
    public static class TransformsPackage3
    {
        static StringBuilder _log;
        static void Log(string s) { _log.AppendLine(s); }
        static void Step(Action a, string what)
        {
            try { a(); Log("  .. " + what + " OK"); }
            catch { Log("  .. " + what + " <-- FAILED HERE"); throw; }
        }

        public static string AddMergeJoinFlow(string pkgPath, string customersCsv,
                                              string contactsCsv, string oleDbConn)
        {
            _log = new StringBuilder();
            try { return Core(pkgPath, customersCsv, contactsCsv, oleDbConn); }
            catch (Exception ex)
            {
                _log.AppendLine();
                _log.AppendLine("*** FAILED after the steps above ***");
                _log.AppendLine(ex.GetType().Name + ": " + ex.Message);
                return _log.ToString();
            }
        }

        static string Core(string pkgPath, string customersCsv, string contactsCsv, string oleDbConn)
        {
            var app = new Application();
            var pkg = app.LoadPackage(pkgPath, null);
            if (pkg.Executables.Contains("DFT_SortAndMergeJoin"))
            { Log("DFT_SortAndMergeJoin already present"); return _log.ToString(); }

            var cmOle = pkg.Connections["CM_SQL_SSISDemo"];

            var cmCust = Ssis.FlatFile(pkg, "CM_FF_Customers3", customersCsv, true, "Delimited");
            foreach (var c in new[] { "CustomerID", "FirstName", "LastName", "Email", "Phone", "SignupDate" })
                Ssis.FlatFileColumn(cmCust, c, ",", 0, 200);
            Ssis.FlatFileColumn(cmCust, "Country", "\r\n", 0, 200);

            var cmCont = Ssis.FlatFile(pkg, "CM_FF_Contacts", contactsCsv, true, "Delimited");
            Ssis.FlatFileColumn(cmCont, "CustomerID", ",", 0, 50);
            Ssis.FlatFileColumn(cmCont, "ContactPhone", ",", 0, 50);
            Ssis.FlatFileColumn(cmCont, "PreferredChannel", "\r\n", 0, 30);
            Log("connections         : CM_FF_Customers3, CM_FF_Contacts");

            var trunc = Ssis.ExecSql(pkg, "SQL_TruncateWithContact", cmOle,
                                     "TRUNCATE TABLE dbo.CustomerWithContact;");
            var dft = Ssis.DataFlow(pkg, "DFT_SortAndMergeJoin");
            Ssis.Link(pkg, trunc, dft, DTSExecResult.Success);
            Ssis.Link(pkg, pkg.Executables["DFT_LookupAndAggregate"], trunc, DTSExecResult.Success);

            MainPipe pipe = Ssis.Pipe(dft);

            // ---------------- LEFT branch: customers -----------------------------
            var srcL = Ssis.Comp(pipe, "FF_SRC_Customers3", "Flat File Source");
            Ssis.SetConn(srcL, pkg, cmCust);
            var sw = srcL.Instantiate();
            sw.AcquireConnections(null); sw.ReinitializeMetaData(); sw.ReleaseConnections();

            var convL = AddIdConversion(pipe, srcL, "DCONV_CustId_L");
            var sortL = AddSort(pipe, convL, "SORT_Customers", "CustomerID_i4");
            Log("left  branch        : source -> Data Conversion -> Sort(CustomerID_i4)");

            // ---------------- RIGHT branch: contacts ------------------------------
            var srcR = Ssis.Comp(pipe, "FF_SRC_Contacts", "Flat File Source");
            Ssis.SetConn(srcR, pkg, cmCont);
            var sw2 = srcR.Instantiate();
            sw2.AcquireConnections(null); sw2.ReinitializeMetaData(); sw2.ReleaseConnections();

            var convR = AddIdConversion(pipe, srcR, "DCONV_CustId_R");
            var sortR = AddSort(pipe, convR, "SORT_Contacts", "CustomerID_i4");
            Log("right branch        : source -> Data Conversion -> Sort(CustomerID_i4)");

            // ---------------- Merge Join -------------------------------------------
            var mj = Ssis.Comp(pipe, "MRG_CustomerContacts", "Merge Join");
            Step(() => Ssis.Connect(pipe, sortL, 0, mj, 0), "attach left  -> Merge Join");
            Step(() => Ssis.Connect(pipe, sortR, 0, mj, 1), "attach right -> Merge Join");
            var mjW = mj.Instantiate();
            // 0 = full outer, 1 = left outer, 2 = inner
            Step(() => mjW.SetComponentProperty("JoinType", 2), "JoinType=Inner");
            // Defaults to 0, which fails validation: "the value for the NumKeyColumns
            // custom property must be between 1 and 1". One sort key => 1.
            Step(() => mjW.SetComponentProperty("NumKeyColumns", 1), "NumKeyColumns=1");

            // select what we need from each side, then project it onto the output
            int lidEmail = SelectOne(mjW, mj, 0, "Email");
            int lidCust  = SelectOne(mjW, mj, 0, "CustomerID_i4");
            int lidPhone = SelectOne(mjW, mj, 1, "ContactPhone");
            int lidChan  = SelectOne(mjW, mj, 1, "PreferredChannel");
            Log("Merge Join          : inner join, 4 columns selected across both inputs");

            // Merge Join AUTO-CREATES an output column for every selected input column.
            // Inserting them by hand duplicates the names and fails with 0xC0208019 -
            // unlike Lookup and Aggregate, which require the columns to be added.
            var produced = new List<string>();
            foreach (IDTSOutputColumn100 oc in mj.OutputCollection[0].OutputColumnCollection)
                produced.Add(oc.Name);
            Log("Merge Join output   : " + produced.Count + " auto-created columns: " +
                string.Join(", ", produced.ToArray()));
            Log("  .. inputs sorted: L=" + sortL.OutputCollection[0].IsSorted +
                " R=" + sortR.OutputCollection[0].IsSorted);

            // ---------------- destination -------------------------------------------
            var dest = Ssis.Comp(pipe, "OLEDST_CustomerWithContact", "OLE DB Destination");
            Ssis.SetConn(dest, pkg, cmOle);
            dest.CustomPropertyCollection["AccessMode"].Value = 3;
            dest.CustomPropertyCollection["OpenRowset"].Value = "[dbo].[CustomerWithContact]";
            Ssis.Connect(pipe, mj, 0, dest, 0);
            var dw = dest.Instantiate();
            dw.AcquireConnections(null); dw.ReinitializeMetaData(); dw.ReleaseConnections();
            int mapped = Ssis.MapDestination(dest, 0,
                new[] { "CustomerID_i4", "Email", "ContactPhone", "PreferredChannel" },
                new[] { "CustomerID",    "Email", "ContactPhone", "PreferredChannel" });
            Log("OLE DB Destination  : dbo.CustomerWithContact, " + mapped + " columns");

            app.SaveToXml(pkgPath, pkg, null);
            Log("");
            Log("saved " + pkgPath);
            return _log.ToString();
        }

        /// Data Conversion turning the CSV's string CustomerID into DT_I4 so the two
        /// branches can be sorted and joined on a numeric key.
        static IDTSComponentMetaData100 AddIdConversion(MainPipe pipe,
                                                        IDTSComponentMetaData100 src, string name)
        {
            var conv = Ssis.Comp(pipe, name, "Data Conversion");
            Ssis.Connect(pipe, src, 0, conv, 0);
            var w = conv.Instantiate();
            var input = conv.InputCollection[0];
            var v = input.GetVirtualInput();
            int lid = 0;
            foreach (IDTSVirtualInputColumn100 vc in v.VirtualInputColumnCollection)
                if (vc.Name == "CustomerID") { lid = vc.LineageID; break; }
            w.SetUsageType(input.ID, v, lid, DTSUsageType.UT_READONLY);
            int outId = conv.OutputCollection[0].ID;
            var oc = w.InsertOutputColumnAt(outId, 0, "CustomerID_i4", "");
            w.SetOutputColumnDataTypeProperties(outId, oc.ID, RTWrap.DataType.DT_I4, 0, 0, 0, 0);
            w.SetOutputColumnProperty(outId, oc.ID, "SourceInputColumnLineageID", lid);
            oc.ErrorRowDisposition = DTSRowDisposition.RD_IgnoreFailure;
            oc.TruncationRowDisposition = DTSRowDisposition.RD_IgnoreFailure;
            return conv;
        }

        /// Sort on one key. Every column must be selected (they all flow through);
        /// NewSortKeyPosition 1 marks the key, 0 marks pass-through columns.
        static IDTSComponentMetaData100 AddSort(MainPipe pipe, IDTSComponentMetaData100 upstream,
                                                string name, string keyColumn)
        {
            var sort = Ssis.Comp(pipe, name, "Sort");
            Ssis.Connect(pipe, upstream, 0, sort, 0);
            var w = sort.Instantiate();
            var input = sort.InputCollection[0];
            var v = input.GetVirtualInput();

            var lids = new List<int>();
            foreach (IDTSVirtualInputColumn100 vc in v.VirtualInputColumnCollection)
                lids.Add(vc.LineageID);
            foreach (int lid in lids)
                w.SetUsageType(input.ID, v, lid, DTSUsageType.UT_READONLY);

            foreach (IDTSInputColumn100 ic in input.InputColumnCollection)
                w.SetInputColumnProperty(input.ID, ic.ID, "NewSortKeyPosition",
                                         ic.Name == keyColumn ? 1 : 0);
            return sort;
        }

        /// Marks one named column on one Merge Join input as used, returning its lineage.
        static int SelectOne(CManagedComponentWrapper w, IDTSComponentMetaData100 cm,
                             int inputIndex, string columnName)
        {
            var input = cm.InputCollection[inputIndex];
            var v = input.GetVirtualInput();
            int lid = 0;
            foreach (IDTSVirtualInputColumn100 vc in v.VirtualInputColumnCollection)
                if (vc.Name == columnName) { lid = vc.LineageID; break; }
            if (lid == 0) throw new Exception("column not found on input " + inputIndex + ": " + columnName);
            w.SetUsageType(input.ID, v, lid, DTSUsageType.UT_READONLY);
            return lid;
        }

        // (No AddMjOutput helper: Merge Join creates its own output columns.)
    }
}
