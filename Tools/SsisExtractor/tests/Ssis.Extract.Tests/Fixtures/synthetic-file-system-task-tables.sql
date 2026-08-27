/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Backs
    Fixtures/SyntheticFileSystemTask.dtsx -- SQL_PreLoad (TRUNCATE) -> a
    pre-load File System Task (Copy) -> DFT_Load (Flat File Source -> Derived
    Column -> OLE DB Destination) -> a post-flow File System Task (Copy).
    Real-world motivation: RBC_Demo_ETL's Package_Advanced.dtsx
    (FST_ArchiveWorkbook), discovered testing ssisx against a real
    ~30-component-type client-shaped portfolio (SSIS_From_Sandeep,
    2026-08-27) -- that File System Task runs pre-load (archives a workbook
    before a Data Flow Task reads it); this fixture adds a post-flow one too
    so both PackagePlanner positions are proven end to end. Safe to
    drop/recreate any time; nothing else in the PoC reads or writes this
    table.

    Run once against .\SQLFORPOC_2022 before regenerating the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-file-system-task-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticFileSystemTaskTarget;
GO
CREATE TABLE dbo.SyntheticFileSystemTaskTarget
(
    ID          INT            NOT NULL,
    Name        NVARCHAR(50)   NOT NULL,
    LoadedAtUtc DATETIME2(3)   NOT NULL
);
GO
