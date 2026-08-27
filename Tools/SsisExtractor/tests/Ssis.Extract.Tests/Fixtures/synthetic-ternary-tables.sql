/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Back a small standalone package
    (Fixtures/SyntheticTernary.dtsx) built to prove ExpressionTranslator's
    ternary (cond ? whenTrue : whenFalse) and unary-negate support end to end,
    added 2026-08-27 -- the same nested shape discovered in RBC_Demo_ETL's own
    Package_Transforms.dtsx (DER_Enrich.EmailDomain/.TenureDays), but built
    from ONLY already-supported building blocks (a numeric comparison, string
    literals, an int literal negation) so it proves ternary/negate in
    isolation, unblocked by that real package's own SEPARATE, still-open gaps
    (SUBSTRING with a non-literal start argument, DATEDIFF). Safe to
    drop/recreate any time; nothing else in the PoC reads or writes these
    tables.

    Seed data spans both ternary branches AND the exact boundary (Amount =
    1000, which the ">" condition routes to "Low", not "High") plus a
    negative Amount so the negate-producing branch of SignFlag is actually
    exercised, not just the literal +1 branch.

    Run once against .\SQLFORPOC_2022 before regenerating the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-ternary-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticTernaryInput;
GO
CREATE TABLE dbo.SyntheticTernaryInput
(
    ID     INT            NOT NULL,
    Amount DECIMAL(12, 2) NOT NULL
);
GO
INSERT INTO dbo.SyntheticTernaryInput (ID, Amount) VALUES
    (1, 500.00),    -- Category=Low, SignFlag=1
    (2, 1500.00),   -- Category=High, SignFlag=1
    (3, -250.00),   -- Category=Low, SignFlag=-1 -- exercises the negate branch
    (4, 1000.00);   -- Category=Low -- exact boundary, "Amount > 1000" is false here
GO

-- ID (not a compound key) deliberately -- see synthetic-conditional-split-tables.sql's own
-- comment for why: PrimaryKeyInference's naming-convention heuristic needs "<table>ID" or "ID".
DROP TABLE IF EXISTS dbo.SyntheticTernaryTarget;
GO
CREATE TABLE dbo.SyntheticTernaryTarget
(
    ID       INT            NOT NULL,
    Amount   DECIMAL(12, 2) NOT NULL,
    Category NVARCHAR(10)   NOT NULL,
    SignFlag INT            NOT NULL
);
GO
