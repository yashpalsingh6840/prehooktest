/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Back a small standalone package
    (Fixtures/SyntheticOleDbSourceTransform.dtsx) built purely to prove ssisx
    generate's OLE DB Source support end to end: a SqlCommand-mode OLE DB
    Source (AccessMode=2) reading dbo.SyntheticOleDbSourceInput, through a
    Derived Column (LoadedAtUtc <- GETUTCDATE(), the same audit-stamp shape
    both real PoC packages use), into dbo.SyntheticOleDbSourceTarget. Safe to
    drop/recreate any time; nothing else in the PoC reads or writes these
    tables.

    Run once against .\SQLFORPOC_2022 before regenerating the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-oledb-source-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticOleDbSourceInput;
GO
CREATE TABLE dbo.SyntheticOleDbSourceInput
(
    ID     INT            NOT NULL,
    Amount DECIMAL(12, 2) NOT NULL
);
GO
INSERT INTO dbo.SyntheticOleDbSourceInput (ID, Amount) VALUES
    (1, 100.50),
    (2, 250.75),
    (3, 999.00);
GO

-- ID (not OrderID) deliberately -- PrimaryKeyInference's naming-convention heuristic looks
-- for "<table>ID" or the fallback "ID", and DbContextEmitter only emits HasKey/
-- ValueGeneratedNever when that heuristic finds a match; EF Core otherwise refuses to build
-- the model at all ("requires a primary key to be defined"), which is what a first version of
-- this fixture (column named OrderID) hit at real run time, not at generate/build time.
DROP TABLE IF EXISTS dbo.SyntheticOleDbSourceTarget;
GO
CREATE TABLE dbo.SyntheticOleDbSourceTarget
(
    ID          INT            NOT NULL,
    Amount      DECIMAL(12, 2) NOT NULL,
    LoadedAtUtc DATETIME2(3)   NOT NULL
);
GO
