/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Back a small standalone package
    (Fixtures/SyntheticIdentifierSanitization.dtsx) built 2026-09-18 purely to
    prove the identifier-sanitization fix end to end: a SqlCommand-mode OLE DB
    Source (AccessMode=2) reading dbo.SyntheticIdentifierSanitizationInput's
    own "Last Cost Price" column -- a real WWI-schema-style name with a
    literal space, matching sql-server-samples/wwi-ssis/DailyETLMain.dtsx's
    own real "WWI Stock Item ID"/"Last Cost Price" columns exactly -- through
    a Derived Column (LoadedAtUtc <- GETUTCDATE(), the same audit-stamp shape
    both real PoC packages use), into dbo.SyntheticIdentifierSanitizationTarget,
    whose own "Last Cost Price" column carries the identical space. Safe to
    drop/recreate any time; nothing else in the PoC reads or writes these
    tables.

    Run once against .\SQLFORPOC_2022 before regenerating the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-identifier-sanitization-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticIdentifierSanitizationInput;
GO
CREATE TABLE dbo.SyntheticIdentifierSanitizationInput
(
    ID                INT            NOT NULL,
    [Last Cost Price] DECIMAL(12, 2) NOT NULL
);
GO
INSERT INTO dbo.SyntheticIdentifierSanitizationInput (ID, [Last Cost Price]) VALUES
    (1, 100.50),
    (2, 250.75),
    (3, 999.00);
GO

-- ID (not e.g. RecordID) deliberately -- PrimaryKeyInference's naming-convention heuristic
-- looks for "<table>ID" or the fallback "ID", and DbContextEmitter only emits HasKey/
-- ValueGeneratedNever when that heuristic finds a match; EF Core otherwise refuses to build
-- the model at all ("requires a primary key to be defined").
DROP TABLE IF EXISTS dbo.SyntheticIdentifierSanitizationTarget;
GO
CREATE TABLE dbo.SyntheticIdentifierSanitizationTarget
(
    ID                INT            NOT NULL,
    [Last Cost Price] DECIMAL(12, 2) NOT NULL,
    LoadedAtUtc       DATETIME2(3)   NOT NULL
);
GO
