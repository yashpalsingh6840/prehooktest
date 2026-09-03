/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Back a small standalone package
    (Fixtures/SyntheticConditionalSplit.dtsx) built purely to prove ssisx
    generate's Conditional Split support end to end: an OLE DB Source
    (SqlCommand mode) reading dbo.SyntheticConditionalSplitInput, through a
    Derived Column (LoadedAtUtc <- GETUTCDATE()), into a Conditional Split
    (one case, "Amount > 1000" -> HighValue; default -> LowValue) fanning out
    to dbo.SyntheticHighValue / dbo.SyntheticLowValue. Safe to drop/recreate
    any time; nothing else in the PoC reads or writes these tables.

    Seed data spans both branches AND the exact boundary (Amount = 1000, which
    the ">" condition routes to LowValue, not HighValue) so a real end-to-end
    run actually exercises routing correctness, not just "some rows moved
    somewhere."

    Run once against .\SQLFORPOC_2022 before regenerating the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-conditional-split-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticConditionalSplitInput;
GO
CREATE TABLE dbo.SyntheticConditionalSplitInput
(
    ID     INT            NOT NULL,
    Amount DECIMAL(12, 2) NOT NULL
);
GO
INSERT INTO dbo.SyntheticConditionalSplitInput (ID, Amount) VALUES
    (1, 500.00),   -- LowValue (default)
    (2, 1500.00),  -- HighValue (case)
    (3, 1000.00),  -- LowValue (default) -- exact boundary, "Amount > 1000" is false here
    (4, 2500.50);  -- HighValue (case)
GO

-- ID (not OrderID) deliberately -- see synthetic-oledb-source-tables.sql's own comment for why:
-- PrimaryKeyInference's naming-convention heuristic needs "<table>ID" or the fallback "ID".
DROP TABLE IF EXISTS dbo.SyntheticHighValue;
GO
CREATE TABLE dbo.SyntheticHighValue
(
    ID          INT            NOT NULL,
    Amount      DECIMAL(12, 2) NOT NULL,
    LoadedAtUtc DATETIME2(3)   NOT NULL
);
GO

DROP TABLE IF EXISTS dbo.SyntheticLowValue;
GO
CREATE TABLE dbo.SyntheticLowValue
(
    ID          INT            NOT NULL,
    Amount      DECIMAL(12, 2) NOT NULL,
    LoadedAtUtc DATETIME2(3)   NOT NULL
);
GO
