/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable. Backs SyntheticForEachFileLoop.dtsx, proving
    ForEach Loop Container / ForEach File Enumerator support (2026-08-28) -- closing RBC_Demo_ETL's
    own FEL_SampleFiles (a single Execute SQL Task, driven by a PropertyExpression referencing the
    loop's own mapped current-file variable, re-run once per matching file).

    dbo.SyntheticForEachFileLoopInput/Target back an ordinary Data Flow Task (DFT_Load) included
    ONLY so the whole package has at least one real Data Flow Task -- ProgramEmitter's own "no
    Data Flow Task could be planned" gate would otherwise reject the whole package before the
    loop's own step ever got a chance to be wired, mirroring the real evidenced package's own
    shape (FEL_SampleFiles sits ALONGSIDE DFT_ExcelImport, not instead of it).

    dbo.SyntheticForEachFileLoopLog is what the loop's own Execute SQL Task inserts into, once per
    file under tests/Ssis.Extract.Tests/Fixtures/synthetic-foreach-file-loop-files/ -- no primary
    key/naming-convention concern here, since this table is never EF-Core-mapped (a plain
    Execute SQL Task's SQL text runs via IUnitOfWork.ExecuteSqlAsync directly, same as any
    pre-load/post-flow SQL statement elsewhere in this tool).

    Run once against .\SQLFORPOC_2022 before generating/executing the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-foreach-file-loop-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticForEachFileLoopInput;
GO
CREATE TABLE dbo.SyntheticForEachFileLoopInput
(
    ID     INT           NOT NULL,
    Amount DECIMAL(12,2) NOT NULL
);
GO
INSERT INTO dbo.SyntheticForEachFileLoopInput (ID, Amount) VALUES (1, 100.00), (2, 250.50);
GO

DROP TABLE IF EXISTS dbo.SyntheticForEachFileLoopTarget;
GO
CREATE TABLE dbo.SyntheticForEachFileLoopTarget
(
    ID          INT           NOT NULL,
    Amount      DECIMAL(12,2) NOT NULL,
    LoadedAtUtc DATETIME2     NOT NULL
);
GO

DROP TABLE IF EXISTS dbo.SyntheticForEachFileLoopLog;
GO
CREATE TABLE dbo.SyntheticForEachFileLoopLog
(
    LogId    INT IDENTITY(1,1) PRIMARY KEY,
    FileName NVARCHAR(260) NOT NULL
);
GO
