#region Namespaces
using System;
using System.Data;
using System.IO;
using System.Data.OleDb;
using Microsoft.SqlServer.Dts.Runtime;
#endregion

namespace ST_ValidateAndLogStart
{
    [Microsoft.SqlServer.Dts.Tasks.ScriptTask.SSISScriptTaskEntryPointAttribute]
    public partial class ScriptMain : Microsoft.SqlServer.Dts.Tasks.ScriptTask.VSTARTScriptObjectModelBase
    {
        // Control Flow Script Task (C#).
        // 1. Confirms the source CSV referenced by CM_FF_Customers actually exists.
        // 2. Stamps User::BatchStartTime.
        // 3. Writes a "Batch Start" row into dbo.EtlAuditLog via the OLE DB connection manager.
        public void Main()
        {
            string filePath = Dts.Connections["CM_FF_Customers"].ConnectionString;

            if (!File.Exists(filePath))
            {
                Dts.Events.FireError(0, "SCR - Validate Source And Log Start",
                    "Source file not found: " + filePath, string.Empty, 0);
                Dts.TaskResult = (int)ScriptResults.Failure;
                return;
            }

            // User::BatchStartTime is a String variable (round-tripped with "o" so the
            // VB.NET task downstream can parse it back unambiguously).
            DateTime startTime = DateTime.Now;
            Dts.Variables["User::BatchStartTime"].Value = startTime.ToString("o");

            OleDbConnection conn = (OleDbConnection)Dts.Connections["CM_SQL_SSISDemo"].AcquireConnection(Dts.Transaction);
            try
            {
                using (OleDbCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO dbo.EtlAuditLog (PackageName, StepName, EventTime, Message) VALUES (?, ?, ?, ?)";
                    cmd.Parameters.AddWithValue("PackageName", Dts.Variables["System::PackageName"].Value.ToString());
                    cmd.Parameters.AddWithValue("StepName", "Batch Start");
                    cmd.Parameters.AddWithValue("EventTime", startTime);
                    cmd.Parameters.AddWithValue("Message", "Source file validated: " + filePath);
                    cmd.ExecuteNonQuery();
                }
            }
            finally
            {
                Dts.Connections["CM_SQL_SSISDemo"].ReleaseConnection(conn);
            }

            Dts.TaskResult = (int)ScriptResults.Success;
        }

        enum ScriptResults
        {
            Success = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Success,
            Failure = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Failure
        };
    }
}
