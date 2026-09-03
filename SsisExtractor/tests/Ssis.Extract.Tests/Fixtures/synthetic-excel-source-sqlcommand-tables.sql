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

-- Backing table for SyntheticExcelSourceSqlCommandWhere.dtsx -- gap-audit Phase 3.4
-- (2026-09-02), a byte-preserving derived edit of SyntheticExcelSourceSqlCommand.dtsx (same
-- reason as that fixture's own note: attaching an OLE DB Destination downstream of a
-- SqlCommand-mode Excel Source fails SSIS's own object-model validation in this environment).
-- Its own SqlCommand is "SELECT * FROM [DripEligibility$] WHERE CustomerID > 5" -- the exact
-- shape and literal values measured against the real checked-in workbook via a raw
-- System.Data.OleDb probe: CustomerID values 6/7/9/10 (4 of the workbook's 9 rows).
DROP TABLE IF EXISTS dbo.SyntheticExcelSourceSqlCommandWhereTarget;
CREATE TABLE dbo.SyntheticExcelSourceSqlCommandWhereTarget (
    CustomerID FLOAT NOT NULL,
    DripEligible VARCHAR(5) NOT NULL,
    ReviewedBy VARCHAR(50) NOT NULL
);
