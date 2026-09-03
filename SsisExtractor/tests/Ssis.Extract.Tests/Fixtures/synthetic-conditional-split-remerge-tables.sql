/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Back a small standalone package
    (Fixtures/SyntheticConditionalSplitRemerge.dtsx) built purely to prove
    ssisx generate's Conditional Split support for the "two branches remerge
    to ONE shared destination via a Union All" shape -- distinct from
    SyntheticConditionalSplit.dtsx, whose two branches each keep their own
    destination. Real-world motivation: RBC_Demo_ETL's Package_Transforms.dtsx
    (DFT_DerivedAndSplit) tags Valid/Invalid rows with their own per-branch
    Derived Column, then a Union All recombines both back into one
    dbo.CustomerEnriched. Safe to drop/recreate any time; nothing else in the
    PoC reads or writes these tables.

    An OLE DB Source (SqlCommand mode) reads dbo.SyntheticRemergeInput, through
    a shared Derived Column (LoadedAtUtc <- GETUTCDATE()) upstream of the
    split, into a Conditional Split (one case, "Amount > 1000" -> High;
    default -> Low). Each branch passes through its OWN Derived Column
    (Segment <- "High" / Segment <- "Low", a literal -- no function-translation
    surface needed, deliberately kept separate from the FINDSTRING/TRIM gap a
    real RBC_Demo_ETL condition also hits), then both feed a Union All that
    recombines into dbo.SyntheticRemergeTarget.

    Seed data spans both branches AND the exact boundary (Amount = 1000, which
    the ">" condition routes to Low, not High) so a real end-to-end run
    actually exercises routing correctness, not just "some rows moved
    somewhere."

    Run once against .\SQLFORPOC_2022 before regenerating the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-conditional-split-remerge-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticRemergeInput;
GO
CREATE TABLE dbo.SyntheticRemergeInput
(
    ID     INT            NOT NULL,
    Amount DECIMAL(12, 2) NOT NULL
);
GO
INSERT INTO dbo.SyntheticRemergeInput (ID, Amount) VALUES
    (1, 500.00),   -- Low (default)
    (2, 1500.00),  -- High (case)
    (3, 1000.00),  -- Low (default) -- exact boundary, "Amount > 1000" is false here
    (4, 2500.50);  -- High (case)
GO

-- ID (not OrderID) deliberately -- see synthetic-oledb-source-tables.sql's own comment for why:
-- PrimaryKeyInference's naming-convention heuristic needs "<table>ID" or the fallback "ID".
DROP TABLE IF EXISTS dbo.SyntheticRemergeTarget;
GO
CREATE TABLE dbo.SyntheticRemergeTarget
(
    ID          INT            NOT NULL,
    Amount      DECIMAL(12, 2) NOT NULL,
    Segment     NVARCHAR(10)   NOT NULL,
    LoadedAtUtc DATETIME2(3)   NOT NULL
);
GO
