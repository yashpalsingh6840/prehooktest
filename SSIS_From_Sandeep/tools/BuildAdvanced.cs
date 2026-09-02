// Builds Package_Advanced.dtsx - the remaining control-flow and adapter components.
//
//   SEQ_Prepare (Sequence Container)
//     +- SQL_TruncateTargets      Execute SQL Task
//     +- FST_ArchiveLog           File System Task (copy the run log)
//   |
//   +-> DFT_ExcelImport      Excel Source -> OLE DB Destination        \  these two run
//   +-> DFT_FlagCustomers    OLE DB Source -> OLE DB Command (per row) /  IN PARALLEL
//
// Also demonstrates: package PARAMETERS (not project ones), an OnError EVENT HANDLER
// writing to dbo.EtlRunLog, a SQL Server LOG PROVIDER, and a container-level
// TRANSACTION - all present in the production suite but missing from the demo.
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.SqlServer.Dts.Runtime;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using RTWrap = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisBuild
{
    public static class AdvancedPackage
    {
        static StringBuilder _log;
        static void Log(string s) { _log.AppendLine(s); }
        static void Step(Action a, string what)
        {
            try { a(); Log("  .. " + what + " OK"); }
            catch { Log("  .. " + what + " <-- FAILED HERE"); throw; }
        }

        public static string Build(string outPath, string xlsxPath, string outDir,
                                   string oleDbConn)
        {
            _log = new StringBuilder();
            try { return Core(outPath, xlsxPath, outDir, oleDbConn); }
            catch (Exception ex)
            {
                _log.AppendLine();
                _log.AppendLine("*** FAILED after the steps above ***");
                _log.AppendLine(ex.GetType().Name + ": " + ex.Message);
                return _log.ToString();
            }
        }

        static string Core(string outPath, string xlsxPath, string outDir, string oleDbConn)
        {
            var pkg = new Package { Name = "Package_Advanced" };

            // Opening a package makes the designer validate its connection managers.
            // This one uses Excel via ACE, which is registered 64-bit ONLY on this
            // machine (no WOW6432Node entry), and it also points at a SQL Server that
            // may be stopped at design time. DelayValidation defers all of that to run
            // time so the package can always be opened and edited.
            pkg.DelayValidation = true;

            // ---------------------------------------------- package PARAMETERS
            // Production FXRate_Import / TRAN_Import use package parameters rather than
            // launcher-injected variables; the demo only had PROJECT parameters.
            // Parameters.Add takes a System.TypeCode, not a pipeline DataType.
            var pExcel = pkg.Parameters.Add("ExcelFilePath", TypeCode.String);
            pExcel.Value = xlsxPath;
            pExcel.Required = false;
            var pBatch = pkg.Parameters.Add("BatchLabel", TypeCode.String);
            pBatch.Value = "nightly";
            pBatch.Required = false;
            Log("package parameters  : ExcelFilePath, BatchLabel");

            var cmOle   = Ssis.OleDb(pkg, "CM_SQL_SSISDemo", oleDbConn);
            var cmExcel = Ssis.Excel(pkg, "CM_EXCEL_Drip", xlsxPath);
            // Archive the INPUT workbook after importing it - the source has to exist at
            // run time, so point at a file that is always present rather than at a log
            // some other package may or may not have written.
            var cmSrcLog = Ssis.FileConn(pkg, "CM_FILE_SourceWorkbook", xlsxPath, 0); // 0 = must exist
            // Archive alongside the other outputs rather than into a sub-folder: a
            // File System Task with CreateFile makes the file but never its parent
            // directory, and driving CreateDirectory needs a folder-typed connection
            // the copy task will not accept ("Type of Connection ... is not a file").
            var cmDstLog = Ssis.FileConn(pkg, "CM_FILE_ArchivedWorkbook",
                               System.IO.Path.Combine(outDir,
                                   "archived-" + System.IO.Path.GetFileName(xlsxPath)), 1);
            Log("connections         : OLEDB, EXCEL (ACE), FILE x2");

            // ------------------------------------------- Sequence Container
            var seq = Ssis.SequenceContainer(pkg, "SEQ_Prepare");
            // container-level transaction, like MarginReport's atomic rename block
            seq.TransactionOption = DTSTransactionOption.NotSupported;
            Log("Sequence Container  : SEQ_Prepare");

            var trunc = (TaskHost)seq.Executables.Add("Microsoft.ExecuteSQLTask");
            trunc.Name = "SQL_TruncateTargets";
            trunc.Properties["Connection"].SetValue(trunc, cmOle.Name);
            trunc.Properties["SqlStatementSource"].SetValue(trunc,
                "TRUNCATE TABLE dbo.DripEligibility; UPDATE dbo.CustomerFlag SET FlagValue='unset';");

            // ------------------------------------------- File System Task
            var fst = (TaskHost)seq.Executables.Add("Microsoft.FileSystemTask");
            fst.Name = "FST_ArchiveWorkbook";
            // DTSFileSystemOperation lives in Microsoft.SqlServer.FileSystemTask.dll,
            // which is not referenced here; 0 = CopyFile.
            fst.Properties["Operation"].SetValue(fst, 0);
            fst.Properties["IsSourcePathVariable"].SetValue(fst, false);
            fst.Properties["Source"].SetValue(fst, cmSrcLog.Name);
            fst.Properties["IsDestinationPathVariable"].SetValue(fst, false);
            fst.Properties["Destination"].SetValue(fst, cmDstLog.Name);
            fst.Properties["OverwriteDestinationFile"].SetValue(fst, true);
            seq.PrecedenceConstraints.Add(trunc, fst).Value = DTSExecResult.Success;
            Log("File System Task    : archive the source workbook into output\\archive");

            // ------------------------------------------- Excel Source flow
            var dftX = Ssis.DataFlow(pkg, "DFT_ExcelImport");
            dftX.DelayValidation = true;
            Ssis.Link(pkg, seq, dftX, DTSExecResult.Success);
            MainPipe px = Ssis.Pipe(dftX);

            var xSrc = Ssis.Comp(px, "EXCEL_SRC_Drip", "Excel Source");
            Ssis.SetConn(xSrc, pkg, cmExcel);
            var xw = xSrc.Instantiate();
            Step(() => xw.SetComponentProperty("AccessMode", 0), "excel AccessMode=Table");
            Step(() => xw.SetComponentProperty("OpenRowset", "DripEligibility$"), "excel OpenRowset");
            xw.AcquireConnections(null);
            xw.ReinitializeMetaData();
            xw.ReleaseConnections();
            Log("Excel Source        : " + xSrc.OutputCollection[0].OutputColumnCollection.Count +
                " columns from [DripEligibility$]");

            var xDst = Ssis.Comp(px, "OLEDST_Drip", "OLE DB Destination");
            Ssis.SetConn(xDst, pkg, cmOle);
            xDst.CustomPropertyCollection["AccessMode"].Value = 3;
            xDst.CustomPropertyCollection["OpenRowset"].Value = "[dbo].[DripEligibility]";
            Ssis.Connect(px, xSrc, 0, xDst, 0);
            var xdw = xDst.Instantiate();
            xdw.AcquireConnections(null); xdw.ReinitializeMetaData(); xdw.ReleaseConnections();
            int mx = Ssis.MapDestination(xDst, 0,
                        new[] { "CustomerID", "DripEligible", "ReviewedBy" },
                        new[] { "CustomerID", "DripEligible", "ReviewedBy" });
            Log("OLE DB Destination  : dbo.DripEligibility, " + mx + " columns");

            // ------------------------------------------- OLE DB Command flow
            // Runs a parameterised UPDATE per row - production blocker C5 in
            // ImportTradeCount. Deliberately reproduced so the N+1 shape is visible.
            var dftC = Ssis.DataFlow(pkg, "DFT_FlagCustomers");
            dftC.DelayValidation = true;
            Ssis.Link(pkg, seq, dftC, DTSExecResult.Success);   // parallel with DFT_ExcelImport
            MainPipe pc = Ssis.Pipe(dftC);

            var cSrc = Ssis.Comp(pc, "OLESRC_CustomerIds", "OLE DB Source");
            Ssis.SetConn(cSrc, pkg, cmOle);
            var cw = cSrc.Instantiate();
            cw.SetComponentProperty("AccessMode", 2);
            cw.SetComponentProperty("SqlCommand",
                "SELECT DISTINCT CustomerID FROM dbo.Orders");
            cw.AcquireConnections(null); cw.ReinitializeMetaData(); cw.ReleaseConnections();

            var cmd = Ssis.Comp(pc, "OLECMD_SetFlag", "OLE DB Command");
            Ssis.SetConn(cmd, pkg, cmOle);
            var cmdW = cmd.Instantiate();
            Step(() => cmdW.SetComponentProperty("SqlCommand",
                    "EXEC dbo.usp_SetCustomerFlag ?, N'flagged'"), "OLE DB Command SqlCommand");
            Ssis.Connect(pc, cSrc, 0, cmd, 0);
            cmdW.AcquireConnections(null);
            cmdW.ReinitializeMetaData();
            cmdW.ReleaseConnections();
            // bind the pipeline's CustomerID to parameter 0 of the statement
            var extNames = new List<string>();
            foreach (IDTSExternalMetadataColumn100 e in cmd.InputCollection[0].ExternalMetadataColumnCollection)
                extNames.Add(e.Name);
            Log("  .. OLE DB Command external columns: " + string.Join(", ", extNames.ToArray()));
            if (extNames.Count == 0) throw new Exception("OLE DB Command exposed no parameters");
            int mapped = Ssis.MapDestination(cmd, 0, new[] { "CustomerID" }, new[] { extNames[0] });
            Log("OLE DB Command      : per-row EXEC usp_SetCustomerFlag, " + mapped + " param bound");

            // ------------------------------------------- Foreach Loop
            // Enumerates sample-data\*.csv and logs each file name. Also demonstrates a
            // PROPERTY EXPRESSION: the task's SQL text is built from the loop variable.
            var app0 = new Application();
            var fel = (ForEachLoop)pkg.Executables.Add("STOCK:FOREACHLOOP");
            fel.Name = "FEL_SampleFiles";
            fel.ForEachEnumerator = app0.ForEachEnumeratorInfos["Foreach File Enumerator"].CreateNew();
            var en = fel.ForEachEnumerator;
            en.Properties["Directory"].SetValue(en, System.IO.Path.GetDirectoryName(xlsxPath));
            en.Properties["FileSpec"].SetValue(en, "*.csv");
            en.Properties["FileNameRetrieval"].SetValue(en, 1);   // 1 = name + extension
            en.Properties["Recurse"].SetValue(en, false);

            pkg.Variables.Add("CurrentFile", false, "User", "");
            var vm = fel.VariableMappings.Add();
            vm.VariableName = "User::CurrentFile";
            vm.ValueIndex = 0;

            var felTask = (TaskHost)fel.Executables.Add("Microsoft.ExecuteSQLTask");
            felTask.Name = "SQL_LogFileName";
            felTask.Properties["Connection"].SetValue(felTask, cmOle.Name);
            felTask.Properties["SqlStatementSource"].SetValue(felTask,
                "INSERT INTO dbo.EtlRunLog (PackageName, SourceName, EventType, MessageText) " +
                "VALUES (N'Package_Advanced', N'FEL_SampleFiles', N'FileFound', N'(pending)');");
            felTask.SetExpression("SqlStatementSource",
                "\"INSERT INTO dbo.EtlRunLog (PackageName, SourceName, EventType, MessageText) " +
                "VALUES (N'Package_Advanced', N'FEL_SampleFiles', N'FileFound', N'\" + " +
                "@[User::CurrentFile] + \"');\"");
            Ssis.Link(pkg, seq, fel, DTSExecResult.Success);
            Log("Foreach Loop        : *.csv in sample-data, logging each name via a property expression");

            // ------------------------------------------- log provider
            var lp = pkg.LogProviders.Add("DTS.LogProviderSQLServer");
            lp.Name = "LP_SqlServer";
            lp.ConfigString = cmOle.Name;
            pkg.LoggingOptions.SelectedLogProviders.Add(lp);
            pkg.LoggingOptions.EventFilterKind = DTSEventFilterKind.Inclusion;
            pkg.LoggingOptions.EventFilter = new string[] { "OnError", "OnWarning", "OnTaskFailed" };
            pkg.LoggingMode = DTSLoggingMode.Enabled;
            Log("Log Provider        : SQL Server (sysssislog), OnError/OnWarning/OnTaskFailed");

            // ------------------------------------------- OnError event handler
            // EventHandlers.Add returns object - cast before touching Executables.
            var eh = (DtsEventHandler)pkg.EventHandlers.Add("OnError");
            var ehTask = (TaskHost)eh.Executables.Add("Microsoft.ExecuteSQLTask");
            ehTask.Name = "SQL_LogOnError";
            ehTask.Properties["Connection"].SetValue(ehTask, cmOle.Name);
            ehTask.Properties["SqlStatementSource"].SetValue(ehTask,
                "INSERT INTO dbo.EtlRunLog (PackageName, SourceName, EventType, MessageText) " +
                "VALUES (N'Package_Advanced', N'(event handler)', N'OnError', N'An error was raised.');");
            Log("Event Handler       : OnError -> INSERT into dbo.EtlRunLog");

            new Application().SaveToXml(outPath, pkg, null);
            Log("");
            Log("saved " + outPath);
            return _log.ToString();
        }
    }
}
