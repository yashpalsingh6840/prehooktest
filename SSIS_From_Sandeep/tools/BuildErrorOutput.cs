// Adds ERROR OUTPUT REDIRECTION to Package.dtsx's customer load.
//
// Production redirects failed rows rather than failing the buffer: CUVImport writes to
// error tables, PN6FTOFPackage to error files, MFDBUpdate to adMutualFund_InvalidRecords.
// The demo previously had no error output at all - a bad row would kill the data flow.
//
//   OLEDST_StagingCustomers  --error output-->  OLEDST_StagingErrors (dbo.StagingCustomers_Errors)
//
// The error output carries every input column plus ErrorCode and ErrorColumn.
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.SqlServer.Dts.Runtime;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;

namespace SsisBuild
{
    public static class ErrorOutputPatch
    {
        static StringBuilder _log;
        static void Log(string s) { _log.AppendLine(s); }

        public static string Apply(string pkgPath)
        {
            _log = new StringBuilder();
            try { return Core(pkgPath); }
            catch (Exception ex)
            {
                _log.AppendLine();
                _log.AppendLine("*** FAILED after the steps above ***");
                _log.AppendLine(ex.GetType().Name + ": " + ex.Message);
                return _log.ToString();
            }
        }

        static string Core(string pkgPath)
        {
            var app = new Application();
            var pkg = app.LoadPackage(pkgPath, null);

            var dft = (TaskHost)pkg.Executables["DFT_LoadCustomers"];
            MainPipe pipe = (MainPipe)dft.InnerObject;

            IDTSComponentMetaData100 dest = null, existing = null;
            foreach (IDTSComponentMetaData100 c in pipe.ComponentMetaDataCollection)
            {
                if (c.Name == "OLEDST_StagingCustomers") dest = c;
                if (c.Name == "OLEDST_StagingErrors") existing = c;
            }
            if (existing != null) { Log("error output already wired - nothing to do"); return _log.ToString(); }
            if (dest == null) throw new Exception("OLEDST_StagingCustomers not found");

            var cmOle = pkg.Connections["CM_SQL_SSISDemo"];

            // 1. tell the destination to REDIRECT failing rows instead of failing
            // Only ErrorRowDisposition. An OLE DB Destination does not truncate, so
            // setting TruncationRowDisposition as well makes it invalid:
            // "has an invalid error or truncation row disposition" -> VS_ISCORRUPT.
            var input = dest.InputCollection[0];
            input.ErrorRowDisposition = DTSRowDisposition.RD_RedirectRow;
            Log("OLEDST_StagingCustomers : ErrorRowDisposition = RedirectRow");

            // 2. new destination fed from the error output
            var errDest = Ssis.Comp(pipe, "OLEDST_StagingErrors", "OLE DB Destination");
            Ssis.SetConn(errDest, pkg, cmOle);
            errDest.CustomPropertyCollection["AccessMode"].Value = 3;
            errDest.CustomPropertyCollection["OpenRowset"].Value = "[dbo].[StagingCustomers_Errors]";

            // the error output is the one flagged IsErrorOut
            IDTSOutput100 errOut = null;
            foreach (IDTSOutput100 o in dest.OutputCollection)
                if (o.IsErrorOut) { errOut = o; break; }
            if (errOut == null) throw new Exception("destination exposes no error output");

            var path = pipe.PathCollection.New();
            path.AttachPathAndPropagateNotifications(errOut, errDest.InputCollection[0]);
            Log("attached error output   -> OLEDST_StagingErrors");

            var w = errDest.Instantiate();
            w.AcquireConnections(null);
            w.ReinitializeMetaData();
            w.ReleaseConnections();

            // what actually arrives on the error path
            var avail = new List<string>();
            foreach (IDTSVirtualInputColumn100 vc in
                     errDest.InputCollection[0].GetVirtualInput().VirtualInputColumnCollection)
                avail.Add(vc.Name);
            Log("error output columns    : " + string.Join(", ", avail.ToArray()));

            // map only what the error table has and the error path provides
            var wanted = new List<string>();
            foreach (var n in new[] { "CustomerID", "FirstName", "LastName", "Email",
                                      "ErrorCode", "ErrorColumn" })
                if (avail.Contains(n)) wanted.Add(n);

            int mapped = Ssis.MapDestination(errDest, 0, wanted.ToArray(), wanted.ToArray());
            Log("OLEDST_StagingErrors    : dbo.StagingCustomers_Errors, " + mapped + " columns");

            app.SaveToXml(pkgPath, pkg, null);
            Log("");
            Log("saved " + pkgPath);
            return _log.ToString();
        }
    }
}
