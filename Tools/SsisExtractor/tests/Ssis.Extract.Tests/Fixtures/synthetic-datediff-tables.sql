/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Back a small standalone package
    (Fixtures/SyntheticDateDiff.dtsx) built to prove ExpressionTranslator's
    DATEDIFF("dd", ...) support end to end, added 2026-08-27, and extended
    the same day to the FULL RBC_Demo_ETL DER_Enrich.TenureDays expression
    (ISNULL(SignupDate) ? -1 : DATEDIFF(...)) once the nullable-value-typed-
    column gap that expression originally surfaced was itself closed
    (NullabilityInference + SqlRowEmitter/SqlRowReaderEmitter/EntityEmitter/
    ExpressionTranslator changes, same session -- see CLAUDE.md's own
    "Nullable value-typed columns" section). SignupDate is nullable again
    here specifically to exercise that fix for real, not just via a unit
    test string comparison.

    Seed dates are computed relative to SYSUTCDATETIME() at seed time, not
    hardcoded literals -- ExpressionTranslator maps GETDATE()/GETUTCDATE()
    BOTH to ctx.LoadedAtUtc (Etl.Core.Abstractions.RowContext's own UTC
    timestamp, set once per run), so the generated code's "now" is UTC even
    though the real SSIS GETDATE() would be server-local time on an actual
    SSIS run -- a pre-existing simplification this fixture works within,
    not something it tests. Computing seed dates the same way keeps the
    fixture stable across whatever day it's actually run on, rather than
    baking in a fixed pair of dates that would drift stale. Includes a
    FUTURE signup date (ID 3, DATEDIFF's negative-result sign convention)
    and a NULL signup date (ID 4, the ISNULL branch, and the actual reason
    this column is nullable at all).

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
    SignupDate DATE NULL
);
GO
INSERT INTO dbo.SyntheticDateDiffInput (ID, SignupDate) VALUES
    (1, DATEADD(day, -10, CAST(SYSUTCDATETIME() AS DATE))),  -- TenureDays = 10
    (2, CAST(SYSUTCDATETIME() AS DATE)),                      -- TenureDays = 0 -- same calendar day
    (3, DATEADD(day, 5, CAST(SYSUTCDATETIME() AS DATE))),     -- TenureDays = -5 -- proves the negative sign convention
    (4, NULL);                                                 -- TenureDays = -1 -- the ISNULL branch
GO

-- ID (not a compound key) deliberately -- see synthetic-conditional-split-tables.sql's own
-- comment for why: PrimaryKeyInference's naming-convention heuristic needs "<table>ID" or "ID".
DROP TABLE IF EXISTS dbo.SyntheticDateDiffTarget;
GO
CREATE TABLE dbo.SyntheticDateDiffTarget
(
    ID         INT  NOT NULL,
    SignupDate DATE NULL,
    TenureDays INT  NOT NULL
);
GO
