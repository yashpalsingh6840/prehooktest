-- Backing tables for tests/Ssis.Extract.Tests/Fixtures/SyntheticLookupSingle.dtsx.
-- The reference table's own key column is deliberately named DIFFERENTLY from the input column
-- (CountryName vs Country), so the fixture proves the join key is genuinely READ from the package
-- rather than guessed by matching names -- which is exactly what a name-matching heuristic would
-- have gotten right by accident.
IF OBJECT_ID('dbo.SyntheticLookupSingleTarget', 'U') IS NOT NULL DROP TABLE dbo.SyntheticLookupSingleTarget;
IF OBJECT_ID('dbo.SyntheticLookupSingleReference', 'U') IS NOT NULL DROP TABLE dbo.SyntheticLookupSingleReference;
IF OBJECT_ID('dbo.SyntheticLookupSingleInput', 'U') IS NOT NULL DROP TABLE dbo.SyntheticLookupSingleInput;
GO

CREATE TABLE dbo.SyntheticLookupSingleInput (
    ID      INT           NOT NULL,
    Country NVARCHAR(100) NOT NULL
);
GO

CREATE TABLE dbo.SyntheticLookupSingleReference (
    CountryName NVARCHAR(100) NOT NULL,
    CountryCode NVARCHAR(10)  NOT NULL,
    Region      NVARCHAR(50)  NOT NULL
);
GO

CREATE TABLE dbo.SyntheticLookupSingleTarget (
    ID          INT           NOT NULL,
    Country     NVARCHAR(100) NOT NULL,
    CountryCode NVARCHAR(10)  NOT NULL,
    Region      NVARCHAR(50)  NOT NULL
);
GO

INSERT dbo.SyntheticLookupSingleInput (ID, Country) VALUES
    (1, N'Canada'),
    (2, N'India'),
    (3, N'Canada');
GO

INSERT dbo.SyntheticLookupSingleReference (CountryName, CountryCode, Region) VALUES
    (N'Canada', N'CA', N'North America'),
    (N'India',  N'IN', N'Asia');
GO
