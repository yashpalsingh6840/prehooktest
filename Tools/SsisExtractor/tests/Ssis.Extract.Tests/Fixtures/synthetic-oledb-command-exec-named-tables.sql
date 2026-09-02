-- Backing table + stored procedure for SyntheticOleDbCommandExecNamed.dtsx (gap-audit Phase
-- 3.1 follow-up): reproduces the REAL evidenced shape from SSIS_From_Sandeep's own
-- Package_Advanced.dtsx (DFT_FlagCustomers, EXEC dbo.usp_SetCustomerFlag ?, N'flagged') --
-- an OLE DB Command that calls a stored procedure by EXEC, whose bound external columns are
-- named after the procedure's own parameters ("@CustomerID"/"@NewStatus"), NOT "Param_N".
--
-- StatusCode is seeded at 0; the flow's source computes NewStatus = CustomerID + 100 (101-103,
-- never overlapping a real CustomerID 1-3) for the same reason as
-- synthetic-oledb-command-reordered-tables.sql: an order regression is unambiguously
-- observable (StatusCode would stay 0, or bind CustomerID into the wrong slot, instead of
-- becoming CustomerID + 100 for every row).
DROP TABLE IF EXISTS dbo.SyntheticOleDbCommandExecNamedTarget;
CREATE TABLE dbo.SyntheticOleDbCommandExecNamedTarget (
    CustomerID INT NOT NULL PRIMARY KEY,
    StatusCode INT NOT NULL
);

INSERT INTO dbo.SyntheticOleDbCommandExecNamedTarget (CustomerID, StatusCode) VALUES
    (1, 0),
    (2, 0),
    (3, 0);
GO

DROP PROCEDURE IF EXISTS dbo.usp_SyntheticSetStatus;
GO
CREATE PROCEDURE dbo.usp_SyntheticSetStatus (@CustomerID INT, @NewStatus INT) AS
BEGIN
    UPDATE dbo.SyntheticOleDbCommandExecNamedTarget SET StatusCode = @NewStatus WHERE CustomerID = @CustomerID;
END
GO
