/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Backs Fixtures/SyntheticCrossDatabaseSource.dtsx
    (Ssis.Extract.FixtureBuilder's "cross-database-source" mode), added 2026-09-17 for the
    gap-audit plan's own Phase 5 (concurrent-whistling-turing.md) -- a genuine cross-database
    Data Flow source, reproducing the real shape found across three GitHub portfolios
    (`dimcustomer`, `fact_sales`, and all 13 of `DailyETLMain`'s own "Extract ... to Staging"
    flows): an OLE DB Source whose own connection manager resolves to a DIFFERENT database than
    the flow's own destination.

    Reproduces the real shape minimally: a SECOND database on the same instance
    (SsisPoC_Secondary, already used by SyntheticSecondConnectionSql.dtsx for the WRITE-side
    case -- this fixture is its read-side mirror) holding the SOURCE table, and the primary
    database (SsisPoC) holding the destination table.

    Run once against .\SQLFORPOC_2022, before building the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-cross-database-source-tables.sql
*/

------------------------------------------------------------ second database, source side
IF DB_ID('SsisPoC_Secondary') IS NULL
    CREATE DATABASE SsisPoC_Secondary;
GO

USE SsisPoC_Secondary;
GO

DROP TABLE IF EXISTS dbo.SyntheticCrossDatabaseSourceInput;
GO
CREATE TABLE dbo.SyntheticCrossDatabaseSourceInput
(
    ID   INT          NOT NULL,
    Name NVARCHAR(50) NOT NULL
);
GO

TRUNCATE TABLE dbo.SyntheticCrossDatabaseSourceInput;
GO
INSERT INTO dbo.SyntheticCrossDatabaseSourceInput (ID, Name) VALUES
    (1, N'Alice'),
    (2, N'Bob'),
    (3, N'Carol');
GO

------------------------------------------------------------ primary database, destination side
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticCrossDatabaseSourceTarget;
GO
CREATE TABLE dbo.SyntheticCrossDatabaseSourceTarget
(
    ID          INT           NOT NULL,
    Name        NVARCHAR(50)  NOT NULL,
    LoadedAtUtc DATETIME2(3)  NOT NULL
);
GO

TRUNCATE TABLE dbo.SyntheticCrossDatabaseSourceTarget;
GO
