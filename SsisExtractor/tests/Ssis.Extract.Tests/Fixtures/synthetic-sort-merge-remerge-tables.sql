/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable. Backs SyntheticSortMergeRemerge.dtsx, proving
    PackagePlanner.ResolveBranch's own chain-walk extension (2026-08-28) for Sort/Merge --
    closing RBC_Demo_ETL's own DFT_MergeSortedBranches (Split -> Sort -> Sort -> Merge -> one
    destination, no per-branch transform at all).

    Deliberately excludes the real package's own Data Conversion (DCONV_MergeId) -- already
    proven separately by synthetic-data-conversion-split-tables.sql. This fixture isolates what's
    actually NEW: that Sort/Merge are genuine name-preserving pass-throughs at RUNTIME, so a
    plain destination column resolves via TransformEmitter's existing "row.PipelineColumnName"
    fallback with zero new value-resolution code.

    Seed data deliberately interleaves Canada/RestOfWorld rows by ID (not grouped), and each
    branch's own two rows are seeded in DESCENDING ID order -- proving Sort's own reordering
    happens for real (if it didn't, the two branches' rows would land in whatever order dbo
    returned them, which happens to already be ascending for a small unindexed SELECT, so this
    ordering is what actually exercises the Sort instead of coincidentally matching it).

    ID (not a compound key) deliberately -- see synthetic-conditional-split-tables.sql's own
    comment for why: PrimaryKeyInference's naming-convention heuristic needs "<table>ID" or "ID".

    Run once against .\SQLFORPOC_2022 before generating/executing the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-sort-merge-remerge-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticSortMergeInput;
GO
CREATE TABLE dbo.SyntheticSortMergeInput
(
    ID       INT          NOT NULL,
    Name     NVARCHAR(50) NOT NULL,
    Category NVARCHAR(50) NOT NULL
);
GO
INSERT INTO dbo.SyntheticSortMergeInput (ID, Name, Category) VALUES
    (3, N'Carol', N'Canada'),
    (1, N'Alice', N'Canada'),
    (4, N'Dave',  N'Mexico'),
    (2, N'Bob',   N'USA');
GO

DROP TABLE IF EXISTS dbo.SyntheticSortMergeTarget;
GO
CREATE TABLE dbo.SyntheticSortMergeTarget
(
    ID          INT          NOT NULL,
    Name        NVARCHAR(50) NOT NULL,
    LoadedAtUtc DATETIME2    NOT NULL
);
GO
