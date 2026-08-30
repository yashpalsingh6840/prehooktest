-- Backing table for SyntheticExcelSourceSqlCommand.dtsx -- a PROBE, built speculatively
-- 2026-08-30 (no real package in the tracked portfolio uses Excel Source in SqlCommand mode;
-- the one real instance, RBC_Demo_ETL's own EXCEL_SRC_Drip, is AccessMode=0/OpenRowset).
-- Identical shape to synthetic-excel-source-tables.sql -- same real checked-in workbook, same
-- column types -- so the two fixtures' own results can be diffed directly to confirm SqlCommand
-- mode's real ACE OLEDB behavior matches OpenRowset mode exactly.
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticExcelSourceSqlCommandTarget;
CREATE TABLE dbo.SyntheticExcelSourceSqlCommandTarget (
    CustomerID FLOAT NOT NULL,
    DripEligible VARCHAR(5) NOT NULL,
    ReviewedBy VARCHAR(50) NOT NULL
);
