/*
    Synthetic fixture tables -- extractor/generator component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable. Backs
    tests/Ssis.Extract.Tests/Fixtures/SyntheticNestedContainer.dtsx, built purely so
    Ssis.Extract.Codegen.PackagePlanner has real, object-model-validated evidence of a
    Sequence Container -- neither PoC package nor any prior synthetic fixture nests a Data
    Flow Task or Execute SQL Task inside a container. Safe to drop/recreate any time; nothing
    else in the PoC reads or writes these tables.

    Run once against .\SQLFORPOC_2022 before regenerating the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-nested-container-tables.sql
*/

USE SsisPoC;
GO

-- Written by DFT_NestedLoad, itself nested inside the Sequence Container SEQ_Load.
DROP TABLE IF EXISTS dbo.SyntheticNestedTarget;
GO
CREATE TABLE dbo.SyntheticNestedTarget
(
    ID          INT            NOT NULL,
    Label       NVARCHAR(50)   NOT NULL,
    Amount      DECIMAL(10, 2) NOT NULL,
    LoadedAtUtc DATETIME2(3)   NOT NULL   -- derived: GETUTCDATE(), same audit-stamp shape as both real PoC packages
);
GO

-- Written by DFT_RootLoad, a root-level Data Flow Task that runs AFTER the Sequence
-- Container (via a precedence constraint on the container itself) -- proves a nested
-- container's flows and a sibling root-level flow land in one correctly ordered list.
DROP TABLE IF EXISTS dbo.SyntheticNestedTarget2;
GO
CREATE TABLE dbo.SyntheticNestedTarget2
(
    ID          INT            NOT NULL,
    Label       NVARCHAR(50)   NOT NULL,
    Amount      DECIMAL(10, 2) NOT NULL,
    LoadedAtUtc DATETIME2(3)   NOT NULL   -- derived: GETUTCDATE(), same audit-stamp shape as both real PoC packages
);
GO
