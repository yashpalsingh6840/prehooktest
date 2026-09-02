Imports System
Imports System.Data
Imports System.Data.SqlClient
Imports Microsoft.SqlServer.Dts.Runtime

' Control Flow Script Task (VB.NET) - runs between the two data flows.
' 1. Reads User::BatchStartTime (a String, round-trip format) and works out elapsed time.
' 2. Counts the rows the first data flow staged, and how many were flagged invalid.
' 3. Writes a "Batch Progress" row into dbo.EtlAuditLog and publishes User::RowsLoaded.
'ScriptMain is the entry point class of the script. Do not change the name, attributes,
'or parent of this class.
<Microsoft.SqlServer.Dts.Tasks.ScriptTask.SSISScriptTaskEntryPointAttribute()> _
<System.CLSCompliantAttribute(False)> _
Partial Public Class ScriptMain
    Inherits Microsoft.SqlServer.Dts.Tasks.ScriptTask.VSTARTScriptObjectModelBase

    Public Sub Main()

        Dim startedAt As DateTime = DateTime.Now
        Dim rawStart As Object = Dts.Variables("User::BatchStartTime").Value

        If rawStart IsNot Nothing AndAlso Not String.IsNullOrEmpty(rawStart.ToString()) Then
            ' Stored by the C# task as ToString("o"); parse it back the same way.
            DateTime.TryParse(rawStart.ToString(), startedAt)
        End If

        Dim elapsed As Double = DateTime.Now.Subtract(startedAt).TotalSeconds

        Dim totalRows As Integer = 0
        Dim invalidRows As Integer = 0

        Dim conn As SqlConnection = CType(Dts.Connections("CM_ADO_SSISDemo").AcquireConnection(Dts.Transaction), SqlConnection)
        Try
            Using cmd As SqlCommand = conn.CreateCommand()
                cmd.CommandText = "SELECT COUNT(*), SUM(CASE WHEN IsValidRow = 0 THEN 1 ELSE 0 END) FROM dbo.StagingCustomers"
                Using reader As SqlDataReader = cmd.ExecuteReader()
                    If reader.Read() Then
                        totalRows = reader.GetInt32(0)
                        If Not reader.IsDBNull(1) Then invalidRows = reader.GetInt32(1)
                    End If
                End Using
            End Using

            Using cmd As SqlCommand = conn.CreateCommand()
                cmd.CommandText = "INSERT INTO dbo.EtlAuditLog (PackageName, StepName, EventTime, RowsAffected, Message) " & _
                                  "VALUES (@PackageName, @StepName, @EventTime, @RowsAffected, @Message)"
                cmd.Parameters.AddWithValue("@PackageName", Dts.Variables("System::PackageName").Value.ToString())
                cmd.Parameters.AddWithValue("@StepName", "Batch Progress")
                cmd.Parameters.AddWithValue("@EventTime", DateTime.Now)
                cmd.Parameters.AddWithValue("@RowsAffected", totalRows)
                cmd.Parameters.AddWithValue("@Message", String.Format( _
                    "Staged {0} row(s), {1} flagged invalid, {2:F2}s after batch start.", _
                    totalRows, invalidRows, elapsed))
                cmd.ExecuteNonQuery()
            End Using
        Finally
            Dts.Connections("CM_ADO_SSISDemo").ReleaseConnection(conn)
        End Try

        Dts.Variables("User::RowsLoaded").Value = totalRows

        Dts.Events.FireInformation(0, "SCR_NotifyAndLogProgress", _
            String.Format("Staged {0} row(s), {1} invalid.", totalRows, invalidRows), _
            String.Empty, 0, False)

        Dts.TaskResult = CInt(ScriptResults.Success)

    End Sub

    Enum ScriptResults
        Success = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Success
        Failure = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Failure
    End Enum

End Class