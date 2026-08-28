/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable. Back a small standalone package
    (Fixtures/SyntheticDataConversionSplit.dtsx) built to prove the two Data-Conversion
    cross-reference paths RBC_Demo_ETL's own Package_Transforms.dtsx (DFT_DerivedAndSplit)
    needs, added 2026-08-28: a Conditional Split condition referencing a Data Conversion output
    column (RouterEmitter), and a post-split Derived Column referencing a Data Conversion
    output column (TransformEmitter). SyntheticDataConversion.dtsx (the earlier, simpler
    fixture) never exercises either -- its converted columns flow straight to a destination,
    referenced by nothing else.

    Seed rows cover both branches and both TenureDays sub-cases: a valid CustomerIdText with a
    valid SignupDateText (Valid branch, real TenureDays), a valid CustomerIdText with a NULL
    SignupDateText (Valid branch, ISNULL(-1) branch), and an invalid/empty CustomerIdText
    (Invalid branch -- !ISNULL(CustomerId_i4) is false, routes here regardless of SignupDateText).

    Run once against .\SQLFORPOC_2022 before generating/executing the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-data-conversion-split-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticDataConversionSplitInput;
GO
CREATE TABLE dbo.SyntheticDataConversionSplitInput
(
    ID              INT           NOT NULL,
    CustomerIdText  NVARCHAR(50)  NULL,
    SignupDateText  NVARCHAR(50)  NULL
);
GO
INSERT INTO dbo.SyntheticDataConversionSplitInput (ID, CustomerIdText, SignupDateText) VALUES
    (1, N'123', DATEADD(day, -10, CAST(SYSUTCDATETIME() AS DATE))),  -- Valid branch, TenureDays = 10
    (2, N'456', NULL),                                                -- Valid branch, TenureDays = -1 (ISNULL branch)
    (3, N'not-a-number', N'2026-01-01');                              -- Invalid branch (CustomerId_i4 fails to parse)
GO

-- ID (not a compound key) deliberately -- see synthetic-conditional-split-tables.sql's own
-- comment for why: PrimaryKeyInference's naming-convention heuristic needs "<table>ID" or "ID".
DROP TABLE IF EXISTS dbo.SyntheticDataConversionSplitTarget;
GO
CREATE TABLE dbo.SyntheticDataConversionSplitTarget
(
    ID             INT  NOT NULL,
    CustomerId_i4  INT  NULL,
    SignupDate_dt  DATE NULL,
    TenureDays     INT  NOT NULL
);
GO
