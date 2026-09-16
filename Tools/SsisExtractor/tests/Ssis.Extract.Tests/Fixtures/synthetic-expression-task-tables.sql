/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable. Backs a small standalone package
    (Fixtures/SyntheticExpressionTask.dtsx) built to prove Microsoft.ExpressionTask ("Expression
    Task") support end to end -- Phase 2 of the unsupported-component-types plan.

    Run once against .\SQLFORPOC_2022 before generating/executing the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-expression-task-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticExpressionTaskTarget;
GO
CREATE TABLE dbo.SyntheticExpressionTaskTarget
(
    ID          INT           NOT NULL,
    Name        NVARCHAR(50)  NOT NULL,
    LoadedAtUtc DATETIME2     NOT NULL
);
GO

DROP TABLE IF EXISTS dbo.SyntheticExpressionTaskLog;
GO
CREATE TABLE dbo.SyntheticExpressionTaskLog
(
    Marker NVARCHAR(100) NOT NULL
);
GO
