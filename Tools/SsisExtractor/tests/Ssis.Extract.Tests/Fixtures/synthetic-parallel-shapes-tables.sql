/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Back a small standalone package
    (Fixtures/SyntheticParallelShapes.dtsx) built purely so the extractor has
    real, object-model-validated evidence of shapes neither PoC package (nor
    the first three synthetic fixtures) ever exercised: real parallel control
    flow, an OLE DB Source -> Destination direct copy with no transform, and a
    Flat File Source's error output routed through a Script Component. See
    Tools/SsisExtractor/docs/report-schema.md "Synthetic parallel-shapes
    fixture". Safe to drop/recreate any time; nothing else in the PoC reads or
    writes these tables.

    Run once against .\SQLFORPOC_2022 before regenerating the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-parallel-shapes-tables.sql
*/

USE SsisPoC;
GO

-- Branch 1: Execute SQL (create-if-missing) -> Flat File Source -> OLE DB Destination
-- -> Execute SQL (post-load update). This table is created by BOTH the setup script here
-- (so the object-model build below can resolve real column metadata via ReinitializeMetaData,
-- the same requirement synthetic-fixture-tables.sql already documents) and, redundantly but
-- harmlessly, by the package's own idempotent "IF OBJECT_ID(...) IS NULL CREATE TABLE" task.
DROP TABLE IF EXISTS dbo.SyntheticLoadATemp;
GO
CREATE TABLE dbo.SyntheticLoadATemp
(
    ID        INT            NOT NULL,
    Name      NVARCHAR(50)   NOT NULL,
    Amount    DECIMAL(12, 2) NOT NULL,
    EntryDate DATE           NOT NULL
);
GO

-- Branch 2: two independent OLE DB Source -> OLE DB Destination pairs in ONE Data Flow
-- Task, no transform in between -- the direct-copy shape from the screenshots.
DROP TABLE IF EXISTS dbo.SyntheticDirectA;
GO
CREATE TABLE dbo.SyntheticDirectA
(
    ID           INT             NOT NULL,
    Label        NVARCHAR(30)    NOT NULL,
    CreatedAtUtc DATETIME2(3)    NOT NULL
);
GO

DROP TABLE IF EXISTS dbo.SyntheticDirectACopy;
GO
CREATE TABLE dbo.SyntheticDirectACopy
(
    ID           INT             NOT NULL,
    Label        NVARCHAR(30)    NOT NULL,
    CreatedAtUtc DATETIME2(3)    NOT NULL
);
GO

DROP TABLE IF EXISTS dbo.SyntheticDirectB;
GO
CREATE TABLE dbo.SyntheticDirectB
(
    ID       INT          NOT NULL,
    Code     NVARCHAR(10) NOT NULL,
    Quantity INT          NOT NULL
);
GO

DROP TABLE IF EXISTS dbo.SyntheticDirectBCopy;
GO
CREATE TABLE dbo.SyntheticDirectBCopy
(
    ID       INT          NOT NULL,
    Code     NVARCHAR(10) NOT NULL,
    Quantity INT          NOT NULL
);
GO

-- Branch 3: Flat File Source's main output -> one destination, its ERROR output routed
-- through a Script Component (passthrough) into a second destination -- error-row
-- redirection, completely unmodeled by the extractor before this fixture.
DROP TABLE IF EXISTS dbo.SyntheticErrorRoutingMain;
GO
CREATE TABLE dbo.SyntheticErrorRoutingMain
(
    ID          INT            NOT NULL,
    Description NVARCHAR(100)  NOT NULL,
    Value       DECIMAL(10, 2) NOT NULL
);
GO

-- Shape matches the Flat File Source's ERROR output exactly, not the main output --
-- confirmed empirically (object-model probe, not assumed): a Flat File Source's error
-- output always carries these three fixed columns, unrelated to the source's own column
-- list. A same-shape-as-main table was tried first and was wrong.
DROP TABLE IF EXISTS dbo.SyntheticErrorRoutingCaught;
GO
CREATE TABLE dbo.SyntheticErrorRoutingCaught
(
    [Flat File Source Error Output Column] VARCHAR(MAX) NULL,  -- DT_TEXT, non-unicode
    ErrorCode                              INT          NOT NULL,
    ErrorColumn                            INT          NOT NULL
);
GO
