/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Back a small standalone package
    (Fixtures/SyntheticDateDiff.dtsx) built to prove ExpressionTranslator's
    DATEDIFF("dd", ...) support end to end, added 2026-08-27.

    Deliberately NOT the full RBC_Demo_ETL DER_Enrich.TenureDays expression
    (ISNULL(SignupDate) ? -1 : DATEDIFF(...)) -- building this fixture
    surfaced a real, separate, pre-existing gap: no SQL row/entity emitter
    (SqlRowEmitter/SqlRowReaderEmitter/EntityEmitter) tracks column
    nullability at all, so a nullable DATE column still generates as a
    non-nullable C# DateOnly, and ISNULL(x)'s own "x is null" translation
    then fails to COMPILE (CS0037) against it -- unrelated to DATEDIFF
    itself (the identical failure would hit any nullable value-typed column,
    e.g. a nullable int, wrapped in ISNULL). See CLAUDE.md's own
    "DATEDIFF..." section for the full account and why it's deliberately
    NOT fixed this round. This fixture's own DATEDIFF ExpressionTranslator
    unit test (ExpressionTranslatorTests.
    TranslateColumn_TranslatesTheRealTenureDaysExpression_...) already
    proves the FULL real expression translates correctly at the text level;
    this fixture instead proves DATEDIFF's own arithmetic (sign convention,
    day-boundary counting) compiles and RUNS correctly end to end, against
    a NOT NULL column, sidestepping the unrelated nullability gap.

    Seed dates are computed relative to SYSUTCDATETIME() at seed time, not
    hardcoded literals -- ExpressionTranslator maps GETDATE()/GETUTCDATE()
    BOTH to ctx.LoadedAtUtc (Etl.Core.Abstractions.RowContext's own UTC
    timestamp, set once per run), so the generated code's "now" is UTC even
    though the real SSIS GETDATE() would be server-local time on an actual
    SSIS run -- a pre-existing simplification this fixture works within,
    not something it tests. Computing seed dates the same way keeps the
    fixture stable across whatever day it's actually run on, rather than
    baking in a fixed pair of dates that would drift stale. Includes a
    FUTURE signup date (ID 3) specifically to exercise DATEDIFF's negative-
    result sign convention, not just the positive case.

    Run once against .\SQLFORPOC_2022, close to the same moment the job
    itself will run (same UTC calendar day), before regenerating the
    fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-datediff-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticDateDiffInput;
GO
CREATE TABLE dbo.SyntheticDateDiffInput
(
    ID         INT  NOT NULL,
    SignupDate DATE NOT NULL
);
GO
INSERT INTO dbo.SyntheticDateDiffInput (ID, SignupDate) VALUES
    (1, DATEADD(day, -10, CAST(SYSUTCDATETIME() AS DATE))),  -- TenureDays = 10
    (2, CAST(SYSUTCDATETIME() AS DATE)),                      -- TenureDays = 0 -- same calendar day
    (3, DATEADD(day, 5, CAST(SYSUTCDATETIME() AS DATE)));     -- TenureDays = -5 -- proves the negative sign convention
GO

-- ID (not a compound key) deliberately -- see synthetic-conditional-split-tables.sql's own
-- comment for why: PrimaryKeyInference's naming-convention heuristic needs "<table>ID" or "ID".
DROP TABLE IF EXISTS dbo.SyntheticDateDiffTarget;
GO
CREATE TABLE dbo.SyntheticDateDiffTarget
(
    ID         INT  NOT NULL,
    SignupDate DATE NOT NULL,
    TenureDays INT  NOT NULL
);
GO
