// Builds Package_Legacy.dtsx - the last production patterns the demo did not cover.
//
// Everything here is taken from a specific package in the N600-ProdProc analysis:
//
//   fixed-width source + header skip  CUVImport      (RaggedRight, 1 header row)
//   append-mode flat file             N600ToSalesForce (writes then appends same file)
//   two databases in one package      FXRate_Import  (wbData + SessionManagement)
//   atomic T-SQL transaction          MarginReport   (3-table sp_rename block)
//   a DISABLED task                   N600ToSalesForce (C1* - present but not running)
//   .dtsConfig configuration          ts_dtsBPSAFeed / CUVImport (C10)
//
// The .dtsConfig itself is written by Build-LegacyPackage.ps1; this file only enables
// package configurations and points at it.
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.SqlServer.Dts.Runtime;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using RTWrap = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisBuild
{
    public static class LegacyPackage
    {
        static StringBuilder _log;
        static void Log(string s) { _log.AppendLine(s); }
        static void Step(Action a, string what)
        {
            try { a(); Log("  .. " + what + " OK"); }
            catch { Log("  .. " + what + " <-- FAILED HERE"); throw; }
        }

        public static string Build(string outPath, string fixedFile, string outDir,
                                   string oleDbConn, string cacheConn, string configPath)
        {
            _log = new StringBuilder();
            try { return Core(outPath, fixedFile, outDir, oleDbConn, cacheConn, configPath); }
            catch (Exception ex)
            {
                _log.AppendLine();
                _log.AppendLine("*** FAILED after the steps above ***");
                _log.AppendLine(ex.GetType().Name + ": " + ex.Message);
                return _log.ToString();
            }
        }

        static string Core(string outPath, string fixedFile, string outDir,
                           string oleDbConn, string cacheConn, string configPath)
        {
            var pkg = new Package { Name = "Package_Legacy" };
            pkg.DelayValidation = true;

            // ---- TWO databases in one package (FXRate_Import / RORPackage) ----------
            var cmMain  = Ssis.OleDb(pkg, "CM_SQL_SSISDemo", oleDbConn);
            var cmCache = Ssis.OleDb(pkg, "CM_SQL_SSISDemoCache", cacheConn);
            Log("connections         : TWO OLE DB databases (SSISDemo + SSISDemoCache)");

            // ---- fixed-width source, skipping a header row (CUVImport) --------------
            var ffFixed = Ssis.FlatFile(pkg, "CM_FF_CustomersFixed", fixedFile, false, "RaggedRight");
            ffFixed.Properties["HeaderRowsToSkip"].SetValue(ffFixed, 1);
            Ssis.FlatFileColumn(ffFixed, "CustomerID",  "", 10, 10);
            Ssis.FlatFileColumn(ffFixed, "FullName",    "", 30, 30);
            Ssis.FlatFileColumn(ffFixed, "Email",       "", 40, 40);
            Ssis.FlatFileColumn(ffFixed, "CountryCode", "\r\n", 0, 4);   // last column ends the row
            Log("flat file (source)  : RaggedRight fixed width 10/30/40/4, HeaderRowsToSkip=1");

            // ---- append-mode destination (N600ToSalesForce) -------------------------
            var ffAppend = Ssis.FlatFile(pkg, "CM_FF_AuditTrail",
                               System.IO.Path.Combine(outDir, "legacy-audit-trail.txt"), false, "Delimited");
            Ssis.FlatFileColumn(ffAppend, "CustomerID",  ",", 0, 10);
            Ssis.FlatFileColumn(ffAppend, "CountryCode", "\r\n", 0, 4);

            // ---- atomic T-SQL transaction (MarginReport) ----------------------------
            var tran = Ssis.ExecSql(pkg, "SQL_AtomicSwap", cmMain, @"
BEGIN TRANSACTION [Tran1];
BEGIN TRY
    TRUNCATE TABLE dbo.CustomerFixedImport;
    COMMIT TRANSACTION [Tran1];
END TRY
BEGIN CATCH
    ROLLBACK TRANSACTION [Tran1];
END CATCH");
            tran.Description = "Atomic BEGIN TRAN / TRY / COMMIT / CATCH ROLLBACK, as MarginReport does";
            Log("Execute SQL Task    : atomic T-SQL transaction block");

            // ---- a DISABLED task (C1* in N600ToSalesForce) --------------------------
            var dead = Ssis.ExecSql(pkg, "SQL_LegacyStep_DISABLED", cmMain,
                           "INSERT INTO dbo.EtlRunLog (PackageName, EventType, MessageText) " +
                           "VALUES (N'Package_Legacy', N'Legacy', N'this step is disabled');");
            dead.Disable = true;
            dead.Description = "Deliberately disabled - mirrors the disabled UTF-8 converter in N600ToSalesForce (C1*)";
            Ssis.Link(pkg, tran, dead, DTSExecResult.Success);
            Log("DISABLED task       : SQL_LegacyStep_DISABLED (Disable=true)");

            // ---- data flow: fixed-width -> main DB, and -> append file --------------
            var dft = Ssis.DataFlow(pkg, "DFT_FixedWidthImport");
            dft.DelayValidation = true;
            Ssis.Link(pkg, tran, dft, DTSExecResult.Success);
            MainPipe pipe = Ssis.Pipe(dft);

            var src = Ssis.Comp(pipe, "FF_SRC_CustomersFixed", "Flat File Source");
            Ssis.SetConn(src, pkg, ffFixed);
            var sw = src.Instantiate();
            sw.AcquireConnections(null); sw.ReinitializeMetaData(); sw.ReleaseConnections();
            Log("Flat File Source    : " + src.OutputCollection[0].OutputColumnCollection.Count + " fixed-width columns");

            // Multicast so the same rows go to the table AND the append file
            var mc = Ssis.Comp(pipe, "MCAST_FixedRows", "Multicast");
            Ssis.Connect(pipe, src, 0, mc, 0);

            var dst = Ssis.Comp(pipe, "OLEDST_CustomerFixedImport", "OLE DB Destination");
            Ssis.SetConn(dst, pkg, cmMain);
            dst.CustomPropertyCollection["AccessMode"].Value = 3;
            dst.CustomPropertyCollection["OpenRowset"].Value = "[dbo].[CustomerFixedImport]";
            var p1 = pipe.PathCollection.New();
            p1.AttachPathAndPropagateNotifications(mc.OutputCollection[0], dst.InputCollection[0]);
            var dw = dst.Instantiate();
            dw.AcquireConnections(null); dw.ReinitializeMetaData(); dw.ReleaseConnections();
            int m1 = Ssis.MapDestination(dst, 0,
                        new[] { "CustomerID", "FullName", "Email", "CountryCode" },
                        new[] { "CustomerID", "FullName", "Email", "CountryCode" });
            Log("OLE DB Destination  : dbo.CustomerFixedImport, " + m1 + " columns");

            var app2 = Ssis.Comp(pipe, "FFDST_AuditTrail_APPEND", "Flat File Destination");
            Ssis.SetConn(app2, pkg, ffAppend);
            // Overwrite=false is what makes this an APPEND, the N600ToSalesForce pattern
            app2.CustomPropertyCollection["Overwrite"].Value = false;
            var p2 = pipe.PathCollection.New();
            p2.AttachPathAndPropagateNotifications(mc.OutputCollection[1], app2.InputCollection[0]);
            var aw = app2.Instantiate();
            aw.AcquireConnections(null); aw.ReinitializeMetaData(); aw.ReleaseConnections();
            int m2 = Ssis.MapDestination(app2, 0,
                        new[] { "CustomerID", "CountryCode" },
                        new[] { "CustomerID", "CountryCode" });
            Log("Flat File Dest      : APPEND mode (Overwrite=false), " + m2 + " columns");

            // ---- write to the SECOND database ---------------------------------------
            var cacheSet = Ssis.ExecSql(pkg, "SQL_CacheSet_SecondDb", cmCache,
                "EXEC dbo.Cache_Set N'LegacyImport', N'fixed-width import completed';");
            cacheSet.Description = "Writes to SSISDemoCache - the second database in this package";
            Ssis.Link(pkg, dft, cacheSet, DTSExecResult.Success);
            Log("Execute SQL Task    : writes to the SECOND database via dbo.Cache_Set");

            // ---- .dtsConfig package configuration (C10) ------------------------------
            pkg.EnableConfigurations = true;
            var cfg = pkg.Configurations.Add();
            cfg.Name = "LegacyXmlConfig";
            cfg.ConfigurationType = DTSConfigurationType.ConfigFile;
            cfg.ConfigurationString = configPath;
            Log("Package config      : XML .dtsConfig -> " + System.IO.Path.GetFileName(configPath));

            new Application().SaveToXml(outPath, pkg, null);
            Log("");
            Log("saved " + outPath);
            return _log.ToString();
        }
    }
}
