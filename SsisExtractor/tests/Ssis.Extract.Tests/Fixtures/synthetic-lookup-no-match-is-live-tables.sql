-- Backing tables for tests/Ssis.Extract.Tests/Fixtures/SyntheticLookupNoMatchIsLive.dtsx.
-- Phase 3 of the gap-audit plan (concurrent-whistling-turing.md, 2026-09-16): the "no-match is
-- the live route" Lookup shape -- the classic "insert-if-new" dimension pattern. The reference
-- table holds names already loaded; the source has a mix of already-existing and genuinely new
-- names, so a real dtexec run of this fixture proves matches are excluded and only misses land.
IF OBJECT_ID('dbo.SyntheticLookupNoMatchIsLiveTarget', 'U') IS NOT NULL DROP TABLE dbo.SyntheticLookupNoMatchIsLiveTarget;
IF OBJECT_ID('dbo.SyntheticLookupNoMatchIsLiveReference', 'U') IS NOT NULL DROP TABLE dbo.SyntheticLookupNoMatchIsLiveReference;
IF OBJECT_ID('dbo.SyntheticLookupNoMatchIsLiveSource', 'U') IS NOT NULL DROP TABLE dbo.SyntheticLookupNoMatchIsLiveSource;
GO

CREATE TABLE dbo.SyntheticLookupNoMatchIsLiveSource (
    ID   INT           NOT NULL,
    Name NVARCHAR(100) NOT NULL
);
GO

CREATE TABLE dbo.SyntheticLookupNoMatchIsLiveReference (
    ExistingName NVARCHAR(100) NOT NULL
);
GO

CREATE TABLE dbo.SyntheticLookupNoMatchIsLiveTarget (
    ID   INT           NOT NULL,
    Name NVARCHAR(100) NOT NULL
);
GO

-- Alice/Carol already exist (must be EXCLUDED); Bob/Dave are genuinely new (must be INSERTED).
INSERT dbo.SyntheticLookupNoMatchIsLiveSource (ID, Name) VALUES
    (1, N'Alice'),
    (2, N'Bob'),
    (3, N'Carol'),
    (4, N'Dave');
GO

INSERT dbo.SyntheticLookupNoMatchIsLiveReference (ExistingName) VALUES
    (N'Alice'),
    (N'Carol');
GO
