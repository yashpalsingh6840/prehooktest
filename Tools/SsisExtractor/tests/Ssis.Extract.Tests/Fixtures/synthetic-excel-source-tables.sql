-- Backing table for SyntheticExcelSource.dtsx (EXCEL_SRC_Drip-shaped Excel Source -> OLE DB
-- Destination, no transform, a genuine direct-copy pipeline). Every column here is mapped
-- from the worksheet -- unlike RBC_Demo_ETL's own dbo.DripEligibility (which also has an
-- unmapped, SQL-defaulted ImportedAt column), since this fixture builder's own
-- ResolveOleDbMetadata helper requires every external column to resolve to a real upstream
-- input column by name; the real package's own unmapped-column shape is already proven
-- separately by ssisx's XML reader (which never goes through this object-model builder at all).
--
-- CustomerID is FLOAT, not INT, deliberately -- matching the worksheet's own real buffer type
-- (r8/double, since Excel/ACE OLEDB has no narrower numeric type at all) exactly, so this
-- fixture proves the CORE Excel Source + direct-copy-to-SQL feature cleanly. RBC_Demo_ETL's own
-- real dbo.DripEligibility.CustomerID IS an INT (a genuine r8-vs-i4 type mismatch on a plain
-- passthrough column) -- a separate, explicitly gapped case (see TransformEmitter.cs's own
-- 2026-08-28 "source column is buffered as X but the destination column is Y" gap), deliberately
-- NOT reproduced here so it doesn't mask this fixture's own, different purpose.
-- No surrogate key column: this table has no column matching EF's own "Id"/"{Type}Id"
-- key-discovery convention or PrimaryKeyInference's naming heuristic (which also requires an
-- INTEGER column, and CustomerID is FLOAT here, matching the worksheet's own real r8 type) --
-- deliberately kept this way, since it's exactly what surfaced DbContextEmitter's own real
-- "requires a primary key to be defined" gap (fixed 2026-08-28 with an explicit HasNoKey()).
DROP TABLE IF EXISTS dbo.SyntheticExcelSourceTarget;
CREATE TABLE dbo.SyntheticExcelSourceTarget (
    CustomerID FLOAT NOT NULL,
    DripEligible VARCHAR(5) NOT NULL,
    ReviewedBy VARCHAR(50) NOT NULL
);
