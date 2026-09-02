#region Namespaces
using System;
using System.Data;
using System.IO;
using System.Data.SqlClient;
using Microsoft.SqlServer.Dts.Runtime;
#endregion

namespace ST_8d84581b262c4e39a00b720e2c6678e0
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

            DateTime startTime = DateTime.Now;

            // User::BatchStartTime is a STRING variable (VARTYPE 8), deliberately - DateTime
            // literals in SSIS are locale-sensitive. Round-trip format keeps it parseable.
            Dts.Variables["User::BatchStartTime"].Value = startTime.ToString("o");

            // A Script Task must use an ADO.NET connection manager: AcquireConnection on a
            // native OLE DB manager returns a COM object that will not cast to
            // OleDbConnection, and its ConnectionString omits the write-only Password.
            SqlConnection conn = (SqlConnection)Dts.Connections["CM_ADO_SSISDemo"].AcquireConnection(Dts.Transaction);
            try
            {                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO dbo.EtlAuditLog (PackageName, StepName, EventTime, Message) VALUES (@PackageName, @StepName, @EventTime, @Message)";
                    cmd.Parameters.AddWithValue("@PackageName", Dts.Variables["System::PackageName"].Value.ToString());
                    cmd.Parameters.AddWithValue("@StepName", "Batch Start");
                    cmd.Parameters.AddWithValue("@EventTime", startTime);
                    cmd.Parameters.AddWithValue("@Message", "Source file validated: " + filePath);
                    cmd.ExecuteNonQuery();
                }
            }
            finally
            {
                Dts.Connections["CM_ADO_SSISDemo"].ReleaseConnection(conn);
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
