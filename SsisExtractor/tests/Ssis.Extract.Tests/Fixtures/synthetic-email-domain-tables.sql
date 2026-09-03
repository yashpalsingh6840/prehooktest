/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Back a small standalone package
    (Fixtures/SyntheticEmailDomain.dtsx) built to prove ExpressionTranslator's
    numeric '+' dispatch and SUBSTRING-with-a-computed-start-argument support
    end to end, added 2026-08-27. Unlike SyntheticFindStringTrim.dtsx and
    SyntheticTernary.dtsx (each of which deliberately used SIMPLER branches to
    isolate one feature at a time), this fixture reproduces RBC_Demo_ETL's own
    Package_Transforms.dtsx (DER_Enrich.EmailDomain) expression VERBATIM --
    FINDSTRING(TRIM(Email),"@",1) > 0 ? SUBSTRING(TRIM(Email),
    FINDSTRING(TRIM(Email),"@",1) + 1,100) : "(none)" -- because by this point
    every piece it needs (FINDSTRING, TRIM, ternary, numeric '+', a computed
    SUBSTRING start) is supported, closing the loop on the real motivating
    case rather than a simplified stand-in. Safe to drop/recreate any time;
    nothing else in the PoC reads or writes these tables.

    Seed data covers all three branches this expression's own logic can take:
    an "@" present with nothing before/after it trimmed away, no "@" at all
    (the ternary's whenFalse branch), and an "@" that only appears after
    trimming leading/trailing whitespace (proving TRIM's own nesting inside
    both FINDSTRING calls actually matters to the result, not just the
    routing decision).

    Run once against .\SQLFORPOC_2022 before regenerating the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-email-domain-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticEmailDomainInput;
GO
CREATE TABLE dbo.SyntheticEmailDomainInput
(
    ID    INT           NOT NULL,
    Email NVARCHAR(200) NOT NULL
);
GO
INSERT INTO dbo.SyntheticEmailDomainInput (ID, Email) VALUES
    (1, N'alice@example.com'),      -- EmailDomain = example.com
    (2, N'bobexample.com'),         -- no "@" -- EmailDomain = (none)
    (3, N'  carol@sample.org  ');   -- EmailDomain = sample.org -- proves TRIM matters to the VALUE, not just routing
GO

-- ID (not a compound key) deliberately -- see synthetic-conditional-split-tables.sql's own
-- comment for why: PrimaryKeyInference's naming-convention heuristic needs "<table>ID" or "ID".
DROP TABLE IF EXISTS dbo.SyntheticEmailDomainTarget;
GO
CREATE TABLE dbo.SyntheticEmailDomainTarget
(
    ID          INT           NOT NULL,
    Email       NVARCHAR(200) NOT NULL,
    EmailDomain NVARCHAR(200) NOT NULL
);
GO
