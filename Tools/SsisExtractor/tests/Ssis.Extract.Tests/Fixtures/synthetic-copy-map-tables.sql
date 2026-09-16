/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable. Backs a small standalone package
    (Fixtures/SyntheticCopyMap.dtsx) built to prove Microsoft.CopyMap ("Copy Column") support
    end to end -- Phase 1 of the unsupported-component-types plan.

    Run once against .\SQLFORPOC_2022 before generating/executing the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-copy-map-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticCopyMapInput;
GO
CREATE TABLE dbo.SyntheticCopyMapInput
(
    ID       INT           NOT NULL,
    FullName NVARCHAR(100) NOT NULL
);
GO
INSERT INTO dbo.SyntheticCopyMapInput (ID, FullName) VALUES
    (1, N'Alice Smith'),
    (2, N'Bob Jones'),
    (3, N'Carol Lee');
GO

DROP TABLE IF EXISTS dbo.SyntheticCopyMapTarget;
GO
CREATE TABLE dbo.SyntheticCopyMapTarget
(
    ID           INT           NOT NULL,
    FullName     NVARCHAR(100) NOT NULL,
    FullNameCopy NVARCHAR(100) NOT NULL
);
GO
