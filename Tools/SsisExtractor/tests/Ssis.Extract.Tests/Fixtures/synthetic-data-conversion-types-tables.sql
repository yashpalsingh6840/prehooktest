/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Backs a small standalone package
    (Fixtures/SyntheticDataConversionTypes.dtsx) built as a real dtexec
    PROBE, added 2026-08-28 -- the original Data Conversion round (same day,
    earlier) only measured DT_I4/DT_DBDATE, the two target types actually
    evidenced in the real SSIS_From_Sandeep portfolio. This round extends
    support SPECULATIVELY, with the user's explicit sign-off, to DT_R8
    (float), DT_WSTR (string, including width-overflow behavior under
    TruncationRowDisposition=IgnoreFailure -- a genuinely different failure
    mode than I4/DBDATE ever hit, since neither has a "truncation" concept),
    and DT_BOOL -- none of which is currently needed by any real package,
    so this is built ahead of demand rather than to close a real gap.

    Run once against .\SQLFORPOC_2022, close to building the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-data-conversion-types-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticDataConversionTypesInput;
GO
CREATE TABLE dbo.SyntheticDataConversionTypesInput
(
    ID       INT           NOT NULL,
    R8Text   NVARCHAR(50)  NULL,
    WstrText NVARCHAR(50)  NULL,
    BoolText NVARCHAR(50)  NULL
);
GO
INSERT INTO dbo.SyntheticDataConversionTypesInput (ID, R8Text, WstrText, BoolText) VALUES
    (1, '3.14',   'Hello',           'True'),  -- ordinary valid values for all three
    (2, ' 2.5 ',  'HelloWorld',      'False'), -- R8: whitespace-padded valid; WSTR: exactly at the target width (10 chars)
    (3, 'abc',    'HelloWorldExtra', '1'),     -- R8: invalid text; WSTR: 15 chars, OVERFLOWS the target width (10)
    (4, '',       '',                '0'),     -- empty string for R8/WSTR; BoolText '0'/'1' numeric-string forms
    (5, NULL,     NULL,              NULL),    -- NULL passthrough for all three
    (6, '1e3',    '  padded  ',      'yes');   -- R8: scientific notation; WSTR: leading/trailing spaces, exactly at width (10); BoolText: invalid text
GO

-- ID (not a compound key) deliberately -- matches every other synthetic fixture's own
-- PrimaryKeyInference-friendly naming convention ("<table>ID" or "ID").
DROP TABLE IF EXISTS dbo.SyntheticDataConversionTypesTarget;
GO
CREATE TABLE dbo.SyntheticDataConversionTypesTarget
(
    ID      INT           NOT NULL,
    ValR8   FLOAT         NULL,
    ValWstr NVARCHAR(10)  NULL,
    ValBool BIT           NULL
);
GO
