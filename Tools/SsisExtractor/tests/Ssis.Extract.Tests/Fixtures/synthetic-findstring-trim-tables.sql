/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Back a small standalone package
    (Fixtures/SyntheticFindStringTrim.dtsx) built to prove ExpressionTranslator's
    FINDSTRING/TRIM support end to end -- both as a VALUE-producing Derived
    Column expression (TrimmedEmail <- TRIM(Email)) and inside a Conditional
    Split condition (FINDSTRING(TRIM(Email),"@",1) > 0), the exact same nested
    shape discovered in RBC_Demo_ETL's own Package_Transforms.dtsx
    (CSPLIT_Validity's "Valid" case), 2026-08-27. Safe to drop/recreate any
    time; nothing else in the PoC reads or writes these tables.

    Seed data deliberately includes leading/trailing whitespace around the "@"
    so TRIM's own effect on the stored TrimmedEmail column is visible (not
    just cosmetic), plus one row with no "@" at all so FINDSTRING's own
    routing decision (Valid vs Invalid) is actually exercised, not just
    "some rows moved somewhere."

    Run once against .\SQLFORPOC_2022 before regenerating the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-findstring-trim-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticFindStringTrimInput;
GO
CREATE TABLE dbo.SyntheticFindStringTrimInput
(
    ID    INT           NOT NULL,
    Email NVARCHAR(200) NOT NULL
);
GO
INSERT INTO dbo.SyntheticFindStringTrimInput (ID, Email) VALUES
    (1, N'  alice@example.com  '),  -- Valid -- proves TRIM strips the padding AND FINDSTRING still finds "@"
    (2, N'bobexample.com'),         -- Invalid -- no "@" at all
    (3, N' carol@sample.org');      -- Valid -- leading whitespace only
GO

-- ID (not a compound key) deliberately -- see synthetic-conditional-split-tables.sql's own
-- comment for why: PrimaryKeyInference's naming-convention heuristic needs "<table>ID" or "ID".
DROP TABLE IF EXISTS dbo.SyntheticFindStringTrimValid;
GO
CREATE TABLE dbo.SyntheticFindStringTrimValid
(
    ID           INT           NOT NULL,
    Email        NVARCHAR(200) NOT NULL,
    TrimmedEmail NVARCHAR(200) NOT NULL
);
GO

DROP TABLE IF EXISTS dbo.SyntheticFindStringTrimInvalid;
GO
CREATE TABLE dbo.SyntheticFindStringTrimInvalid
(
    ID           INT           NOT NULL,
    Email        NVARCHAR(200) NOT NULL,
    TrimmedEmail NVARCHAR(200) NOT NULL
);
GO
