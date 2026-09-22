/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Backs Fixtures/SyntheticErrorRedirect.dtsx
    (Ssis.Extract.FixtureBuilder's "error-redirect" mode), added to close a real bug found
    reviewing RBC_Demo_ETL's own generated Package.dtsx: DFT_LoadCustomers's own
    OLEDST_StagingErrors (fed by OLEDST_StagingCustomers's error output,
    ErrorRowDisposition=RedirectRow) was silently dropped -- no generated file, no gap. See
    CLAUDE.md's own account.

    Reproduces the real shape minimally: a primary target table with a UNIQUE constraint on
    Code, seeded with two rows sharing the same Code so the second one fails to insert with a
    genuine duplicate-key violation (errorOrTruncationOperation="Insert", not a truncation --
    the cleanest, most unambiguous way to exercise ErrorRowDisposition=RedirectRow for real).
    The error table deliberately mirrors the real OLEDST_StagingErrors shape: ErrorRowID
    (identity PK) and FailedAt (DB-side default) are never mapped from the pipeline at all --
    matching the real .dtsx exactly, and exercising EntityEmitter's own extraProperties path.

    Run once against .\SQLFORPOC_2022, before building the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-error-redirect-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticErrorRedirectSource;
GO
CREATE TABLE dbo.SyntheticErrorRedirectSource
(
    ID   INT           NOT NULL,
    Code NVARCHAR(20)  NOT NULL,
    Name NVARCHAR(50)  NOT NULL
);
GO

-- Three good rows plus one deliberate duplicate of Code='C002' -- the fourth row (ID=4) is the
-- one real SSIS will redirect; the other three, including the FIRST 'C002' row (ID=2), land fine.
INSERT INTO dbo.SyntheticErrorRedirectSource (ID, Code, Name) VALUES
    (1, N'C001', N'Alice'),
    (2, N'C002', N'Bob'),
    (3, N'C003', N'Carol'),
    (4, N'C002', N'Duplicate Bob');
GO

DROP TABLE IF EXISTS dbo.SyntheticErrorRedirectTarget;
GO
CREATE TABLE dbo.SyntheticErrorRedirectTarget
(
    Code NVARCHAR(20) NOT NULL UNIQUE,
    Name NVARCHAR(50) NOT NULL
);
GO

DROP TABLE IF EXISTS dbo.SyntheticErrorRedirectErrors;
GO
CREATE TABLE dbo.SyntheticErrorRedirectErrors
(
    ErrorRowID INT           IDENTITY(1,1) PRIMARY KEY,
    Code       NVARCHAR(20)  NULL,
    Name       NVARCHAR(50)  NULL,
    ErrorCode  INT           NULL,
    ErrorColumn INT          NULL,
    FailedAt   DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
