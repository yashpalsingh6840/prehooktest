-- Backing tables for SyntheticMergeInterleaveProbe.dtsx / SyntheticUnionTwoSources.dtsx --
-- gap-audit Phase 3.6 (2026-09-02). Two GENUINELY INDEPENDENT tables (not one source split by a
-- Conditional Split, unlike every prior Merge/UnionAll fixture in this repo) with a deliberately
-- DISJOINT, INTERLEAVED ID range -- Left holds the odd IDs, Right the even ones -- so a merged/
-- unioned output's own row order settles, by direct readback, whether Microsoft.Merge performs a
-- true sort-preserving interleave (1,2,3,4,5,6) or plain concatenation after independent sorts
-- (1,3,5,2,4,6), and whether Microsoft.UnionAll preserves each side's own row order untouched.
-- Seed rows deliberately NOT in ID order, so each fixture's own Sort (Merge probe) or ORDER BY
-- (UnionAll probe) is genuinely exercised rather than coincidentally already correct.
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticMergeProbeLeft;
CREATE TABLE dbo.SyntheticMergeProbeLeft (
    ID INT NOT NULL,
    Name VARCHAR(20) NOT NULL
);

INSERT INTO dbo.SyntheticMergeProbeLeft (ID, Name) VALUES
    (5, N'L5'),
    (1, N'L1'),
    (3, N'L3');

DROP TABLE IF EXISTS dbo.SyntheticMergeProbeRight;
CREATE TABLE dbo.SyntheticMergeProbeRight (
    ID INT NOT NULL,
    Name VARCHAR(20) NOT NULL
);

INSERT INTO dbo.SyntheticMergeProbeRight (ID, Name) VALUES
    (6, N'R6'),
    (2, N'R2'),
    (4, N'R4');
