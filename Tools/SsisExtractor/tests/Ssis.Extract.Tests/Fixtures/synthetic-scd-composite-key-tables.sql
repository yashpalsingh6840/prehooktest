-- Backing tables for tests/Ssis.Extract.Tests/Fixtures/SyntheticScdCompositeKey.dtsx.
-- Phase 4 of the gap-audit plan (concurrent-whistling-turing.md, 2026-09-16): measures how a
-- Microsoft.SCD component matches on a COMPOSITE (2-column) business key.
--
-- The dimension has exactly one current row, RegionCode='EAST'/StoreCode='001'. The source has
-- three rows designed to distinguish AND-of-both-columns from either single-column-only
-- hypothesis (see BuildScdCompositeKeyFixture's own doc comment for the full reasoning):
--   1. RegionCode='EAST', StoreCode='001' (exact match, same StoreName)  -> must be Unchanged
--   2. RegionCode='EAST', StoreCode='999' (shares RegionCode only)       -> must be New under AND
--   3. RegionCode='ZZZZ', StoreCode='001' (shares StoreCode only)        -> must be New under AND
IF OBJECT_ID('dbo.SyntheticScdCompositeOutNew', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdCompositeOutNew;
IF OBJECT_ID('dbo.SyntheticScdCompositeOutUnchanged', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdCompositeOutUnchanged;
IF OBJECT_ID('dbo.SyntheticScdCompositeDim', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdCompositeDim;
IF OBJECT_ID('dbo.SyntheticScdCompositeSource', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdCompositeSource;
GO

CREATE TABLE dbo.SyntheticScdCompositeSource (
    RegionCode NVARCHAR(10)  NOT NULL,
    StoreCode  NVARCHAR(10)  NOT NULL,
    StoreName  NVARCHAR(100) NOT NULL
);
GO

CREATE TABLE dbo.SyntheticScdCompositeDim (
    Id         INT           IDENTITY(1,1) PRIMARY KEY,
    RegionCode NVARCHAR(10)  NOT NULL,
    StoreCode  NVARCHAR(10)  NOT NULL,
    StoreName  NVARCHAR(100) NOT NULL,
    StartDate  DATETIME      NULL,
    EndDate    DATETIME      NULL
);
GO

CREATE TABLE dbo.SyntheticScdCompositeOutUnchanged (
    RegionCode NVARCHAR(10)  NOT NULL,
    StoreCode  NVARCHAR(10)  NOT NULL,
    StoreName  NVARCHAR(100) NOT NULL
);
GO

CREATE TABLE dbo.SyntheticScdCompositeOutNew (
    RegionCode NVARCHAR(10)  NOT NULL,
    StoreCode  NVARCHAR(10)  NOT NULL,
    StoreName  NVARCHAR(100) NOT NULL
);
GO

INSERT dbo.SyntheticScdCompositeDim (RegionCode, StoreCode, StoreName, StartDate, EndDate) VALUES
    (N'EAST', N'001', N'Original', DATEADD(day, -30, GETDATE()), NULL);
GO

INSERT dbo.SyntheticScdCompositeSource (RegionCode, StoreCode, StoreName) VALUES
    (N'EAST', N'001', N'Original'),
    (N'EAST', N'999', N'SharesRegionOnly'),
    (N'ZZZZ', N'001', N'SharesStoreOnly');
GO
