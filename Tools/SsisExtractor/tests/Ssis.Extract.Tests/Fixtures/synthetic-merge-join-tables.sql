/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable. Backs SyntheticMergeJoin.dtsx (and its two
    byte-preserving derived variants), which prove Microsoft.Sort/Microsoft.MergeJoin support
    end to end -- the gap discovered testing ssisx against a real third-party portfolio
    (SSIS_From_Sandeep's Package_Transforms.dtsx -- DFT_SortAndMergeJoin: two Flat File Sources,
    each Sort'd on a Data-Conversion-derived int key, joined via Microsoft.MergeJoin with the
    real evidenced JoinType raw value 2).

    Deliberately SQL-sourced here (not Flat File + Data Conversion, unlike the real package) to
    isolate Sort/MergeJoin's own mechanics -- Data Conversion's own cross-reference into a
    Merge Join was proven separately.

    WHAT THIS SEED DATA IS FOR (gap-audit Phase 1, 2026-09-02)
    ---------------------------------------------------------
    JoinType's raw-value-to-semantics mapping had never been measured. The codegen layer
    hardcoded MergeJoinType.LeftOuter for EVERY Merge Join regardless of the raw value, and the
    belief that raw 2 meant LeftOuter was inferred from the real destination's column shape, not
    observed. Running this fixture under real dtexec once per raw value settled it:

        raw 0 = FULL OUTER    raw 1 = LEFT OUTER    raw 2 = INNER

    So the one real evidenced package (JoinType=2) had been generating an INNER join as a LEFT
    OUTER one -- emitting unmatched left rows, with a nulled right side, that real SSIS drops.

    The keys are chosen so ROW COUNT ALONE separates all three join types:
        Left  keys 1,2,3  (Name A,B,C)      -- key 1 is LEFT-only
        Right keys 2,3,4  (Phone P2,P3,P4)  -- key 4 is RIGHT-only
    Expected target contents per join type:
        INNER      (raw 2) -> 2 rows: (2,B,P2) (3,C,P3)
        LEFT OUTER (raw 1) -> 3 rows: (1,A,NULL) (2,B,P2) (3,C,P3)
        FULL OUTER (raw 0) -> 4 rows: (1,A,NULL) (2,B,P2) (3,C,P3) (4,NULL,P4)

    Name is therefore NULLABLE: under FULL OUTER the right-only key 4 has no left row at all,
    so Name genuinely arrives NULL. (It was NOT NULL while raw 2 was believed to be LeftOuter,
    a shape under which that case can never occur.)

    Run once against .\SQLFORPOC_2022 before generating/executing the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-merge-join-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticMergeJoinLeft;
GO
CREATE TABLE dbo.SyntheticMergeJoinLeft
(
    ID   INT          NOT NULL,
    Name NVARCHAR(50) NOT NULL
);
GO
INSERT INTO dbo.SyntheticMergeJoinLeft (ID, Name) VALUES (1, N'A'), (2, N'B'), (3, N'C');
GO

DROP TABLE IF EXISTS dbo.SyntheticMergeJoinRight;
GO
CREATE TABLE dbo.SyntheticMergeJoinRight
(
    ID    INT          NOT NULL,
    Phone NVARCHAR(50) NOT NULL
);
GO
INSERT INTO dbo.SyntheticMergeJoinRight (ID, Phone) VALUES (2, N'P2'), (3, N'P3'), (4, N'P4');
GO

-- ID (not a compound key) deliberately -- see synthetic-conditional-split-tables.sql's own
-- comment for why: PrimaryKeyInference's naming-convention heuristic needs "<table>ID" or "ID".
DROP TABLE IF EXISTS dbo.SyntheticMergeJoinTarget;
GO
CREATE TABLE dbo.SyntheticMergeJoinTarget
(
    ID    INT          NOT NULL,
    Name  NVARCHAR(50) NULL,
    Phone NVARCHAR(50) NULL
);
GO
