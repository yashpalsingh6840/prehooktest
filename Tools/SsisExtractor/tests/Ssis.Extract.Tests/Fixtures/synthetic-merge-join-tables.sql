/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable. Was meant to back a small standalone package
    proving Microsoft.Sort/Microsoft.MergeJoin support end to end (the gap discovered testing
    ssisx against a real third-party portfolio, SSIS_From_Sandeep's Package_Transforms.dtsx --
    DFT_SortAndMergeJoin: two Flat File Sources, each Sort'd on a Data-Conversion-derived int
    key, joined via Microsoft.MergeJoin with the real evidenced JoinType raw value 2).

    The .dtsx fixture itself was NOT completed -- Ssis.Extract.FixtureBuilder's own
    BuildMergeJoinFixture hit an unresolved SSIS object-model quirk (see that method's own doc
    comment for the full account) before producing a clean, checked-in shape. Kept here anyway
    as a documented starting point: the seed data itself (a left-only key and a right-only key,
    to distinguish LeftOuter from FullOuter by behavior) is still exactly right for whoever
    continues this. Sort/MergeJoin's own runtime join algorithm was instead verified via a plain
    C# unit test with no SSIS involved at all
    (D:\PoC\SSIS_Rewrite\tests\Etl.Core.Tests\Data\MergeJoinRowSourceTests.cs), and the real
    evidenced package's own extraction/codegen/build correctness was proven end to end
    separately -- see CLAUDE.md's own Merge Join section.

    Deliberately SQL-sourced here (not Flat File + Data Conversion, unlike the real package) to
    isolate Sort/MergeJoin's own mechanics -- Data Conversion's own cross-reference into a
    Merge Join was ALREADY proven structurally against the real package's own regenerated
    build (0 warnings/0 errors); this fixture's own job is to prove the join's RUNTIME
    behavior for real, since JoinType's raw-value-to-semantics mapping had no independent
    verification before this.

    Left has keys 1,2,3 (Name A,B,C); Right has keys 2,3,4 (Phone P2,P3,P4) -- key 1 is
    LEFT-only (proves an unmatched left row surfaces with a NULL right side, if JoinType=2
    really is LeftOuter) and key 4 is RIGHT-only (proves an unmatched right row is DROPPED
    entirely, not just nulled -- the fact that actually distinguishes LeftOuter from FullOuter).

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
    Name  NVARCHAR(50) NOT NULL,
    Phone NVARCHAR(50) NULL
);
GO
