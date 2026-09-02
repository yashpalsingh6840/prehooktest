// Builds Package_Exports.dtsx - the export-side components the demo lacked.
//
//   DFT_ExportDelimited   OLE DB Source  -> Flat File Destination (comma, header)
//   DFT_ExportFixedWidth  OLE DB Source  -> Flat File Destination (fixed width)
//   DFT_AdoNetRoundTrip   ADO NET Source -> ADO NET Destination
//
// Production writes flat files in ~15 of the 44 packages (DSAstExport, RORPackage,
// DSMonthlyStats, PN6LSTAT, MODReport...) and uses ADO.NET adapters in
// MODReportDaily_DI / SFReport / LoginStatsReport_DI. A FILE connection manager is
// declared for the run log, matching MarginReport / FXRate_Import / TRAN_Import.
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.SqlServer.Dts.Runtime;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using RTWrap = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisBuild
{
    public static class ExportsPackage
    {
        static StringBuilder _log;
        static void Log(string s) { _log.AppendLine(s); }
        static void Step(Action a, string what)
        {
            try { a(); Log("  .. " + what + " OK"); }
            catch { Log("  .. " + what + " <-- FAILED HERE"); throw; }
        }

        public static string Build(string outPath, string outDir, string oleDbConn, string adoConn)
        {
            _log = new StringBuilder();
            try { return Core(outPath, outDir, oleDbConn, adoConn); }
            catch (Exception ex)
            {
                _log.AppendLine();
                _log.AppendLine("*** FAILED after the steps above ***");
                _log.AppendLine(ex.GetType().Name + ": " + ex.Message);
                return _log.ToString();
            }
        }

        static string Core(string outPath, string outDir, string oleDbConn, string adoConn)
        {
            var pkg = new Package { Name = "Package_Exports" };

            var cmOle = Ssis.OleDb(pkg, "CM_SQL_SSISDemo", oleDbConn);
            var cmAdo = Ssis.AdoNet(pkg, "CM_ADO_SSISDemo", adoConn);

            // FILE connection manager - production uses one for run logs.
            var cmLog = Ssis.FileConn(pkg, "CM_FILE_RunLog",
                            System.IO.Path.Combine(outDir, "exports-run.log"), 1);
            Log("connections         : OLEDB, ADO.NET, FILE (run log)");

            // ---------------------------------------------- 1. delimited export
            var ffDelim = Ssis.FlatFile(pkg, "CM_FF_ExportDelimited",
                              System.IO.Path.Combine(outDir, "customers-export.csv"), true, "Delimited");
            Ssis.FlatFileColumn(ffDelim, "CustomerID", ",", 0, 50);
            Ssis.FlatFileColumn(ffDelim, "FullName", ",", 0, 200);
            Ssis.FlatFileColumn(ffDelim, "CleanEmail", "\r\n", 0, 200);

            BuildExportFlow(pkg, "DFT_ExportDelimited", cmOle, ffDelim,
                "SELECT CustomerID, FullName, CleanEmail FROM dbo.StagingCustomers WHERE IsValidRow = 1",
                new[] { "CustomerID", "FullName", "CleanEmail" }, null);
            Log("DFT_ExportDelimited : OLE DB Source -> Flat File Destination (comma)");

            // ------------------------------------------- 2. fixed-width export
            // Production CUVImport reads fixed width; this writes it, same idea.
            var ffFixed = Ssis.FlatFile(pkg, "CM_FF_ExportFixed",
                              System.IO.Path.Combine(outDir, "customers-fixed.txt"), false, "FixedWidth");
            Ssis.FlatFileColumn(ffFixed, "CustomerID", "", 10, 10);
            Ssis.FlatFileColumn(ffFixed, "FullName",   "", 30, 30);
            Ssis.FlatFileColumn(ffFixed, "CleanEmail", "", 40, 40);
            // a fixed-width file still needs something to end the row
            Ssis.FlatFileColumn(ffFixed, "RowEnd", "\r\n", 0, 1);

            BuildExportFlow(pkg, "DFT_ExportFixedWidth", cmOle, ffFixed,
                "SELECT CustomerID, FullName, CleanEmail, '' AS RowEnd FROM dbo.StagingCustomers WHERE IsValidRow = 1",
                new[] { "CustomerID", "FullName", "CleanEmail", "RowEnd" }, null);
            Log("DFT_ExportFixedWidth: OLE DB Source -> Flat File Destination (fixed width)");

            // ------------------------------------------- 3. ADO.NET round trip
            var trunc = Ssis.ExecSql(pkg, "SQL_TruncateExportLog", cmOle,
                                     "TRUNCATE TABLE dbo.CustomerExportLog;");
            var dft3 = Ssis.DataFlow(pkg, "DFT_AdoNetRoundTrip");
            Ssis.Link(pkg, trunc, dft3, DTSExecResult.Success);
            MainPipe p3 = Ssis.Pipe(dft3);

            var aSrc = Ssis.Comp(p3, "ADO_SRC_Customers", "ADO NET Source");
            Ssis.SetConn(aSrc, pkg, cmAdo);
            var aSrcW = aSrc.Instantiate();
            Step(() => aSrcW.SetComponentProperty("SqlCommand",
                    "SELECT CustomerID, FullName FROM dbo.StagingCustomers WHERE IsValidRow = 1"),
                 "ADO source SqlCommand");
            // ADO NET Source AccessMode: 0 = table or view, 1 = SQL command.
            // Supplying SqlCommand while AccessMode is 0 fails with 0xC0202100.
            Step(() => aSrcW.SetComponentProperty("AccessMode", 1), "ADO source AccessMode=SqlCommand");
            aSrcW.AcquireConnections(null);
            aSrcW.ReinitializeMetaData();
            aSrcW.ReleaseConnections();
            Log("ADO NET Source      : " + aSrc.OutputCollection[0].OutputColumnCollection.Count + " columns");

            var aDst = Ssis.Comp(p3, "ADO_DST_ExportLog", "ADO NET Destination");
            Ssis.SetConn(aDst, pkg, cmAdo);
            var aDstW = aDst.Instantiate();
            Step(() => aDstW.SetComponentProperty("TableOrViewName", "\"dbo\".\"CustomerExportLog\""),
                 "ADO dest TableOrViewName");
            Ssis.Connect(p3, aSrc, 0, aDst, 0);
            aDstW.AcquireConnections(null);
            aDstW.ReinitializeMetaData();
            aDstW.ReleaseConnections();
            int m3 = Ssis.MapDestination(aDst, 0,
                        new[] { "CustomerID", "FullName" },
                        new[] { "CustomerID", "FullName" });
            Log("ADO NET Destination : dbo.CustomerExportLog, " + m3 + " columns");

            new Application().SaveToXml(outPath, pkg, null);
            Log("");
            Log("saved " + outPath);
            return _log.ToString();
        }

        /// OLE DB Source -> Flat File Destination, mapping straight through by name.
        static void BuildExportFlow(Package pkg, string dftName, ConnectionManager cmOle,
                                    ConnectionManager cmFF, string sql, string[] columns,
                                    string unusedPlaceholder)
        {
            var dft = Ssis.DataFlow(pkg, dftName);
            MainPipe pipe = Ssis.Pipe(dft);

            var src = Ssis.Comp(pipe, dftName + "_SRC", "OLE DB Source");
            Ssis.SetConn(src, pkg, cmOle);
            var sw = src.Instantiate();
            sw.SetComponentProperty("AccessMode", 2);           // 2 = SQL command
            sw.SetComponentProperty("SqlCommand", sql);
            sw.AcquireConnections(null);
            sw.ReinitializeMetaData();
            sw.ReleaseConnections();

            var dst = Ssis.Comp(pipe, dftName + "_DST", "Flat File Destination");
            Ssis.SetConn(dst, pkg, cmFF);
            dst.CustomPropertyCollection["Overwrite"].Value = true;
            Ssis.Connect(pipe, src, 0, dst, 0);
            var dw = dst.Instantiate();
            dw.AcquireConnections(null);
            dw.ReinitializeMetaData();
            dw.ReleaseConnections();
            Ssis.MapDestination(dst, 0, columns, columns);
        }
    }
}
