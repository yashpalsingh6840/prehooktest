-- Backing tables for SyntheticScriptTaskSeams.dtsx (ssisx generate --seams, Script Task half).
--
--   SQL_Truncate -> SCR_Start -> DFT_Load -> SCR_Finish
--
-- SCR_Start writes User::Marker; SCR_Finish reads it back and records it here alongside the row
-- count it observes. A run is only correct if dbo.SyntheticScriptTaskLog ends up holding the
-- marker SCR_Start set -- that is what proves the shared PackageVariables actually carried a
-- value between two independently-written Script Task fills, which nothing else in Etl.Core does.
--
-- The row count SCR_Finish reads must also be 2, and it can only be 2 if the SELECT sees rows the
-- flow inserted but has not committed -- i.e. that a Script Task's own SQL really does run inside
-- the package's single shared transaction.
--
-- Run against .\SQLFORPOC_2022 / SsisPoC.
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticScriptTaskLog;
DROP TABLE IF EXISTS dbo.SyntheticScriptTaskTarget;
GO

CREATE TABLE dbo.SyntheticScriptTaskTarget
(
    ID          INT           NOT NULL PRIMARY KEY,
    Name        NVARCHAR(50)  NOT NULL,
    LoadedAtUtc DATETIME2(7)  NOT NULL
);
GO

CREATE TABLE dbo.SyntheticScriptTaskLog
(
    Marker    NVARCHAR(100) NOT NULL,
    RowsSeen  INT           NOT NULL
);
GO
