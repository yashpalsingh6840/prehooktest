-- Backing table for SyntheticForEachDataFlowLoop.dtsx -- built speculatively 2026-08-30 (no real
-- package in the tracked portfolio has a ForEach Loop whose body is a Data Flow Task; the one
-- real ForEach Loop, RBC_Demo_ETL's own FEL_SampleFiles, has a single Execute SQL Task body
-- instead -- see SyntheticForEachFileLoop.dtsx / synthetic-foreach-file-loop-tables.sql).
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticForEachDataFlowLoopTarget;
CREATE TABLE dbo.SyntheticForEachDataFlowLoopTarget (
    ID INT NOT NULL,
    Name NVARCHAR(50) NOT NULL,
    LoadedAtUtc DATETIME2 NOT NULL
);
