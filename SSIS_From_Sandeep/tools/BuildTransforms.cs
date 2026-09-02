// Builds Package_Transforms.dtsx entirely inside C#.
//
// WHY THE WHOLE PIPELINE IS BUILT HERE RATHER THAN FROM POWERSHELL
// ----------------------------------------------------------------
// Pipeline objects are COM. A MainPipe returned to PowerShell comes back as
// System.__ComObject and cannot be passed to a method typed as MainPipe
// ("Cannot convert ... System.__ComObject#{2ad35903-...}"). Managed runtime types
// (Package, TaskHost, ConnectionManager) marshal fine, COM pipeline types do not.
// So every IDTSComponentMetaData100 / MainPipe touch happens inside one C# call.
using System;
using System.Text;
using Microsoft.SqlServer.Dts.Runtime;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using RTWrap = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisBuild
{
    public static class TransformsPackage
    {
        static StringBuilder _log;
        static void Log(string s) { _log.AppendLine(s); }

        /// Wrapper so a COM failure still returns the progress log - otherwise the
        /// HRESULT alone gives no clue which component call failed.
        public static string Build(string outPath, string csvPath, string oleDbConn)
        {
            _log = new StringBuilder();
            try { return BuildCore(outPath, csvPath, oleDbConn); }
            catch (Exception ex)
            {
                _log.AppendLine();
                _log.AppendLine("*** FAILED after the steps above ***");
                _log.AppendLine(ex.GetType().Name + ": " + ex.Message);
                return _log.ToString();
            }
        }

        static string BuildCore(string outPath, string csvPath, string oleDbConn)
        {
            var pkg = new Package { Name = "Package_Transforms" };

            // ------------------------------------------------------ connections
            var cmOle = Ssis.OleDb(pkg, "CM_SQL_SSISDemo", oleDbConn);
            var cmFF  = Ssis.FlatFile(pkg, "CM_FF_Customers", csvPath, true, "Delimited");
            string[] cols = { "CustomerID", "FirstName", "LastName", "Email", "Phone", "SignupDate" };
            foreach (var c in cols) Ssis.FlatFileColumn(cmFF, c, ",", 0, 200);
            Ssis.FlatFileColumn(cmFF, "Country", "\r\n", 0, 200);  // last column ends the row
            Log("connections         : CM_SQL_SSISDemo (OLEDB), CM_FF_Customers (FLATFILE, 7 cols)");

            // -------------------------------------------------- reset the target
            var trunc = Ssis.ExecSql(pkg, "SQL_TruncateEnriched", cmOle,
                                     "TRUNCATE TABLE dbo.CustomerEnriched;");

            // ------------------------------------------------------- data flow
            var dft  = Ssis.DataFlow(pkg, "DFT_DerivedAndSplit");
            Ssis.Link(pkg, trunc, dft, DTSExecResult.Success);
            MainPipe pipe = Ssis.Pipe(dft);

            // --- Flat File Source ---------------------------------------------
            var src  = Ssis.Comp(pipe, "FF_SRC_Customers", "Flat File Source");
            Ssis.SetConn(src, pkg, cmFF);
            var srcW = src.Instantiate();
            srcW.AcquireConnections(null);
            srcW.ReinitializeMetaData();
            srcW.ReleaseConnections();
            Log("Flat File Source    : " + src.OutputCollection[0].OutputColumnCollection.Count + " columns");

            // --- Data Conversion ----------------------------------------------
            var conv = Ssis.Comp(pipe, "DCONV_Types", "Data Conversion");
            Ssis.Connect(pipe, src, 0, conv, 0);
            var convW  = conv.Instantiate();
            var convIn = conv.InputCollection[0];
            var cvIn   = convIn.GetVirtualInput();
            int lidCustomerId = 0, lidSignup = 0;
            foreach (IDTSVirtualInputColumn100 vc in cvIn.VirtualInputColumnCollection)
            {
                if (vc.Name == "CustomerID" || vc.Name == "SignupDate")
                {
                    convW.SetUsageType(convIn.ID, cvIn, vc.LineageID, DTSUsageType.UT_READONLY);
                    if (vc.Name == "CustomerID") lidCustomerId = vc.LineageID; else lidSignup = vc.LineageID;
                }
            }
            int convOutId = conv.OutputCollection[0].ID;
            // CustomerID -> DT_I4. One CSV row has a blank id, so ignore the failure
            // (yields NULL) instead of failing the whole buffer.
            AddConverted(convW, convOutId, 0, "CustomerID_i4", RTWrap.DataType.DT_I4, 0, lidCustomerId);
            AddConverted(convW, convOutId, 1, "SignupDate_dt", RTWrap.DataType.DT_DBDATE, 0, lidSignup);
            Log("Data Conversion     : CustomerID->DT_I4, SignupDate->DT_DBDATE (RD_IgnoreFailure)");

            // --- Derived Column -----------------------------------------------
            var der = Ssis.Comp(pipe, "DER_Enrich", "Derived Column");
            Ssis.Connect(pipe, conv, 0, der, 0);
            var derW = der.Instantiate();
            // Select ONLY the columns the expressions reference. A column marked
            // READONLY but unreferenced is a validation error; unselected columns
            // still flow through the component untouched.
            SelectInputColumns(derW, der, 0, new[] { "FirstName", "LastName", "Email", "SignupDate_dt" });

            // Explicit (DT_WSTR,200) casts: concatenating two 200-char columns yields a
            // 401-char expression type, which mismatches the 200-char target column and
            // fails the destination with VS_NEEDSNEWMETADATA.
            AddDerived(derW, der, "FullName",
                "(DT_WSTR,200)(TRIM([FirstName]) + \" \" + TRIM([LastName]))",
                RTWrap.DataType.DT_WSTR, 200);
            AddDerived(derW, der, "EmailDomain",
                "(DT_WSTR,200)(FINDSTRING(TRIM([Email]),\"@\",1) > 0 ? SUBSTRING(TRIM([Email])," +
                "FINDSTRING(TRIM([Email]),\"@\",1) + 1,100) : \"(none)\")",
                RTWrap.DataType.DT_WSTR, 200);
            AddDerived(derW, der, "TenureDays",
                "ISNULL([SignupDate_dt]) ? -1 : DATEDIFF(\"dd\",[SignupDate_dt],GETDATE())",
                RTWrap.DataType.DT_I4, 0);
            Log("Derived Column      : FullName, EmailDomain, TenureDays");

            // --- Conditional Split ---------------------------------------------
            var split = Ssis.Comp(pipe, "CSPLIT_Validity", "Conditional Split");
            Ssis.Connect(pipe, der, 0, split, 0);
            var splitW = split.Instantiate();
            SelectInputColumns(splitW, split, 0, new[] { "CustomerID_i4", "Email" });

            string validExpr = "!ISNULL([CustomerID_i4]) && FINDSTRING(TRIM([Email]),\"@\",1) > 0";
            // InsertOutput takes a placement + an anchor output id; the pre-existing
            // output remains the DEFAULT branch and must stay last.
            var branch = splitW.InsertOutput(DTSInsertPlacement.IP_BEFORE,
                                             split.OutputCollection[0].ID);
            branch.Name = "Valid";
            splitW.SetOutputProperty(branch.ID, "Expression", validExpr);
            splitW.SetOutputProperty(branch.ID, "FriendlyExpression", validExpr);
            split.OutputCollection[1].Name = "Invalid";
            Log("Conditional Split   : 'Valid' branch + 'Invalid' default");

            // --- Derived Column on each branch, tagging its origin --------------
            var tagV = TagBranch(pipe, split, 0, "DER_TagValid",   "Valid");
            var tagI = TagBranch(pipe, split, 1, "DER_TagInvalid", "Invalid");
            Log("Derived Column x2   : RowSource tag per branch");

            // --- Union All ------------------------------------------------------
            var union = Ssis.Comp(pipe, "UNION_Recombine", "Union All");
            // Union All grows itself: attaching a path to its last input automatically
            // creates the next one. Calling InputCollection.New() as well produces a
            // duplicate name plus a dangling extra input, so just attach to what exists.
            Ssis.Connect(pipe, tagV, 0, union, 0);
            var p2 = pipe.PathCollection.New();
            p2.AttachPathAndPropagateNotifications(tagI.OutputCollection[0],
                                                   union.InputCollection[1]);
            // Only the FIRST input's columns define the output and map automatically.
            // Every later input must be mapped by hand via OutputColumnLineageID, or its
            // values arrive NULL (RowSource was silently NULL on the Invalid branch).
            int unionMapped = MapUnionInput(union, 1);
            Log("Union All           : " + union.InputCollection.Count + " inputs, " +
                unionMapped + " columns mapped on input 2");

            // --- OLE DB Destination ---------------------------------------------
            var dest = Ssis.Comp(pipe, "OLEDST_CustomerEnriched", "OLE DB Destination");
            Ssis.SetConn(dest, pkg, cmOle);
            dest.CustomPropertyCollection["AccessMode"].Value = 3;              // fast load
            dest.CustomPropertyCollection["OpenRowset"].Value = "[dbo].[CustomerEnriched]";
            Ssis.Connect(pipe, union, 0, dest, 0);
            var destW = dest.Instantiate();
            destW.AcquireConnections(null);
            destW.ReinitializeMetaData();
            destW.ReleaseConnections();
            int mapped = Ssis.MapDestination(dest, 0,
                new[] { "CustomerID_i4", "FullName", "EmailDomain", "SignupDate_dt", "TenureDays", "RowSource" },
                new[] { "CustomerID",    "FullName", "EmailDomain", "SignupDate",    "TenureDays", "RowSource" });
            Log("OLE DB Destination  : dbo.CustomerEnriched, " + mapped + " columns mapped explicitly");

            // ------------------------------------------------------------- save
            new Application().SaveToXml(outPath, pkg, null);
            Log("");
            Log("saved " + outPath);
            return _log.ToString();
        }

        /// Adds one Derived Column output column. Properties go through the wrapper's
        /// Set*Property methods so the component is notified - assigning
        /// CustomPropertyCollection[..].Value directly does not always take effect.
        static void AddDerived(CManagedComponentWrapper w, IDTSComponentMetaData100 cm,
                               string name, string expr, RTWrap.DataType dt, int len)
        {
            var outp = cm.OutputCollection[0];
            var oc = w.InsertOutputColumnAt(outp.ID, outp.OutputColumnCollection.Count, name, "");
            w.SetOutputColumnDataTypeProperties(outp.ID, oc.ID, dt, len, 0, 0, 0);
            w.SetOutputColumnProperty(outp.ID, oc.ID, "Expression", expr);
            w.SetOutputColumnProperty(outp.ID, oc.ID, "FriendlyExpression", expr);
        }

        /// Adds one Data Conversion output column bound to an input column's lineage.
        static void AddConverted(CManagedComponentWrapper w, int outputId, int index,
                                 string name, RTWrap.DataType dt, int len, int sourceLineageId)
        {
            var oc = w.InsertOutputColumnAt(outputId, index, name, "");
            w.SetOutputColumnDataTypeProperties(outputId, oc.ID, dt, len, 0, 0, 0);
            w.SetOutputColumnProperty(outputId, oc.ID, "SourceInputColumnLineageID", sourceLineageId);
            oc.ErrorRowDisposition      = DTSRowDisposition.RD_IgnoreFailure;
            oc.TruncationRowDisposition = DTSRowDisposition.RD_IgnoreFailure;
        }

        /// A Derived Column hung off one Conditional Split output, stamping RowSource.
        static IDTSComponentMetaData100 TagBranch(MainPipe pipe, IDTSComponentMetaData100 split,
                                                  int outIndex, string name, string tag)
        {
            var d = Ssis.Comp(pipe, name, "Derived Column");
            var path = pipe.PathCollection.New();
            path.AttachPathAndPropagateNotifications(split.OutputCollection[outIndex],
                                                     d.InputCollection[0]);
            var w = d.Instantiate();
            // RowSource is a constant expression - it references no input column, so
            // none are selected.
            AddDerived(w, d, "RowSource", "\"" + tag + "\"", RTWrap.DataType.DT_WSTR, 30);
            return d;
        }

        /// Selects and maps every column of a secondary Union All input onto the
        /// matching output column (matched by name) via OutputColumnLineageID.
        static int MapUnionInput(IDTSComponentMetaData100 union, int inputIndex)
        {
            var w = union.Instantiate();
            var input = union.InputCollection[inputIndex];
            var vIn = input.GetVirtualInput();

            var ids = new System.Collections.Generic.List<int>();
            foreach (IDTSVirtualInputColumn100 vc in vIn.VirtualInputColumnCollection)
                ids.Add(vc.LineageID);
            foreach (int lid in ids)
                w.SetUsageType(input.ID, vIn, lid, DTSUsageType.UT_READONLY);

            int mapped = 0;
            var outp = union.OutputCollection[0];
            foreach (IDTSInputColumn100 ic in input.InputColumnCollection)
            {
                foreach (IDTSOutputColumn100 oc in outp.OutputColumnCollection)
                {
                    if (string.Equals(oc.Name, ic.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        w.SetInputColumnProperty(input.ID, ic.ID,
                                                 "OutputColumnLineageID", oc.LineageID);
                        mapped++;
                        break;
                    }
                }
            }
            return mapped;
        }

        /// Marks exactly the named input columns READONLY. Selecting a column that no
        /// expression references makes the component fail validation.
        static void SelectInputColumns(CManagedComponentWrapper w, IDTSComponentMetaData100 cm,
                                       int inputIndex, string[] names)
        {
            var input = cm.InputCollection[inputIndex];
            var vIn = input.GetVirtualInput();       // one instance, reused - see SsisBuilder
            var wanted = new System.Collections.Generic.List<int>();
            foreach (IDTSVirtualInputColumn100 vc in vIn.VirtualInputColumnCollection)
                if (Array.IndexOf(names, vc.Name) >= 0) wanted.Add(vc.LineageID);
            foreach (int lid in wanted)
                w.SetUsageType(input.ID, vIn, lid, DTSUsageType.UT_READONLY);
        }
    }
}
