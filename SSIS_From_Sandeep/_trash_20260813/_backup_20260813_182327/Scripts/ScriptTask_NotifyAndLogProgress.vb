Imports System
Imports System.Data
Imports System.Data.OleDb
Imports Microsoft.SqlServer.Dts.Runtime

<Microsoft.SqlServer.Dts.Tasks.ScriptTask.SSISScriptTaskEntryPointAttribute()> _
<System.CLSCompliantAttribute(False)> _
Partial Public Class ScriptMain
    Inherits Microsoft.SqlServer.Dts.Tasks.ScriptTask.VSTARTScriptObjectModelBase

    ' Control Flow Script Task (VB.NET).
    ' Reads User::RowsLoaded (populated by the Row Count transform in
    ' DFT - Load Customers From CSV) and User::BatchStartTime, then writes a
    ' progress row into dbo.EtlAuditLog and fires an information event.
    Public Sub Main()
        Dim rowsLoaded As Integer = CInt(Dts.Variables("User::RowsLoaded").Value)
        ' User::BatchStartTime is a String variable written by the C# task using "o".
        Dim startTime As DateTime = DateTime.Parse(Dts.Variables("User::BatchStartTime").Value.ToString())
        Dim elapsed As TimeSpan = DateTime.Now.Subtract(startTime)

        Dim conn As OleDbConnection = CType(Dts.Connections("CM_SQL_SSISDemo").AcquireConnection(Dts.Transaction), OleDbConnection)
        Try
            Dim cmd As OleDbCommand = conn.CreateCommand()
            cmd.CommandText = "INSERT INTO dbo.EtlAuditLog (PackageName, StepName, EventTime, RowsAffected, Message) VALUES (?, ?, ?, ?, ?)"
            cmd.Parameters.AddWithValue("PackageName", Dts.Variables("System::PackageName").Value.ToString())
            cmd.Parameters.AddWithValue("StepName", "Customers Loaded")
            cmd.Parameters.AddWithValue("EventTime", DateTime.Now)
            cmd.Parameters.AddWithValue("RowsAffected", rowsLoaded)
            cmd.Parameters.AddWithValue("Message", "Loaded " & rowsLoaded.ToString() & " customer rows in " & elapsed.TotalSeconds.ToString("0.0") & " seconds")
            cmd.ExecuteNonQuery()
        Finally
            Dts.Connections("CM_SQL_SSISDemo").ReleaseConnection(conn)
        End Try

        Dts.Events.FireInformation(0, "SCR - Notify And Log Progress", "Loaded " & rowsLoaded.ToString() & " rows.", String.Empty, 0, True)
        Dts.TaskResult = ScriptResults.Success
    End Sub

    Enum ScriptResults
        Success = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Success
        Failure = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Failure
    End Enum

End Class
