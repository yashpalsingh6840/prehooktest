/*
    Synthetic fixture table -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Backs a small standalone package
    (Fixtures/SyntheticPostFlowSql.dtsx) built purely to prove ssisx generate's
    "Execute SQL Task after a Data Flow Task" support end to end, since
    SyntheticParallelShapes.dtsx's own post-flow Execute SQL Task (Branch 1)
    follows a direct Flat File Source -> OLE DB Destination with no Derived
    Column, so it never gets wired into a generated Program.cs (a separate,
    unrelated, already-documented gap) -- this fixture's Data Flow Task DOES
    have a Derived Column, so it is the one that can prove the post-flow SQL
    step end to end: generate, build, run, and see SQL_PostLoad's own UPDATE
    actually take effect after the load. Safe to drop/recreate any time;
    nothing else in the PoC reads or writes this table.

    Run once against .\SQLFORPOC_2022 before regenerating the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-post-flow-sql-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticPostFlowTarget;
GO
CREATE TABLE dbo.SyntheticPostFlowTarget
(
    ID          INT             NOT NULL,
    Name        NVARCHAR(50)    NOT NULL,
    LoadedAtUtc DATETIME2(3)    NOT NULL
);
GO
