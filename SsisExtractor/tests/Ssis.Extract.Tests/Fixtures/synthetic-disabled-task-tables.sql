-- Backing tables for SyntheticDisabledTask.dtsx (a disabled task must be SKIPPED).
--
-- The package is a single fully-ordered chain, deliberately with no parallelism at all, so the
-- only thing this fixture measures is the disabled-skip:
--
--   SQL_MarkPre -> SQL_Disabled_MarkNever -> DFT_Load -> SQL_MarkPost -> SEQ_Disabled
--                  (DTS:Disabled="True")                                (DTS:Disabled="True")
--                                                                        +- SQL_InsideDisabledSeq
--
-- After a correct run dbo.SyntheticDisabledTaskLog must hold EXACTLY 'pre' and 'post'. A
-- 'disabled-ran' row means the disabled task executed; an 'inside-disabled-seq' row means a
-- disabled CONTAINER's children were executed. Either is the bug.
--
-- It also answers the question the codegen fix depends on: whether a disabled task's own
-- SUCCESSORS still run. If 'post' is absent (and the target table empty), a disabled task
-- halts its branch and skipping just that one node would be wrong.
--
-- Run against .\SQLFORPOC_2022 / SsisPoC.
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticDisabledTaskLog;
DROP TABLE IF EXISTS dbo.SyntheticDisabledTaskTarget;
GO

CREATE TABLE dbo.SyntheticDisabledTaskLog
(
    Marker NVARCHAR(50) NOT NULL
);
GO

CREATE TABLE dbo.SyntheticDisabledTaskTarget
(
    ID          INT           NOT NULL PRIMARY KEY,
    Name        NVARCHAR(50)  NOT NULL,
    LoadedAtUtc DATETIME2(7)  NOT NULL
);
GO
