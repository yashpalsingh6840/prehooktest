/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Backs Fixtures/SyntheticSecondConnectionSql.dtsx
    (Ssis.Extract.FixtureBuilder's "second-connection-sql" mode), added 2026-09-01 to close a
    real bug found running RBC_Demo_ETL's own Package_Legacy.dtsx end to end: its
    SQL_CacheSet_SecondDb task runs `EXEC dbo.Cache_Set ...` against CM_SQL_SSISDemoCache -- a
    genuinely different database from every other connection manager in that package
    (CM_SQL_SSISDemo). Before this fix, every Execute SQL Task ran through the package's ONE
    IUnitOfWork connection unconditionally, so this task's own statement silently ran against
    the WRONG database.

    Reproduces the real shape minimally: a primary database (this one, SsisPoC, standing in for
    SSISDemo) that a Data Flow Task loads into, and a SECOND database on the same instance
    (SsisPoC_Secondary, standing in for SSISDemoCache) that a post-flow Execute SQL Task writes
    to directly -- no stored procedure dependency, unlike the real Cache_Set, since that detail
    is orthogonal to what this fixture proves (which connection a task's own SQL actually runs
    against).

    Run once against .\SQLFORPOC_2022, before building the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-second-connection-sql-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticSecondConnectionTarget;
GO
CREATE TABLE dbo.SyntheticSecondConnectionTarget
(
    ID         INT           NOT NULL,
    Name       NVARCHAR(50)  NOT NULL,
    LoadedAtUtc DATETIME2(3) NOT NULL
);
GO

------------------------------------------------------------ second database
IF DB_ID('SsisPoC_Secondary') IS NULL
    CREATE DATABASE SsisPoC_Secondary;
GO

USE SsisPoC_Secondary;
GO

DROP TABLE IF EXISTS dbo.SyntheticSecondConnectionLog;
GO
CREATE TABLE dbo.SyntheticSecondConnectionLog
(
    LogKey   NVARCHAR(100) NOT NULL PRIMARY KEY,
    LogValue NVARCHAR(200) NULL,
    SetAt    DATETIME      NOT NULL DEFAULT GETDATE()
);
GO
