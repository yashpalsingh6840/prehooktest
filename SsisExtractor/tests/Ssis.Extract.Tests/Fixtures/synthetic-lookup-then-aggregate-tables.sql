-- Backing tables for SyntheticLookupThenAggregate.dtsx -- built 2026-09-02, the real evidenced
-- Lookup+Aggregate composed shape (RBC_Demo_ETL's own DFT_LookupAndAggregate): a Lookup whose
-- Match output feeds a Multicast, one branch of which feeds an Aggregate, with the Lookup's own
-- No-Match output AND the Multicast's own other branch both discarded (dead-end RowCounts).
--
-- Seed data deliberately includes one row (CustomerID=4, Country='Atlantis') whose Country has NO
-- match in the reference table -- exercising the discard/filter path directly: with
-- NoMatchBehavior=1 (redirect), this row must be excluded from the aggregate entirely, not
-- grouped under a null/sentinel Region and not counted anywhere.
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticLookupThenAggregateSource;
CREATE TABLE dbo.SyntheticLookupThenAggregateSource (
    Country NVARCHAR(50) NOT NULL,
    CustomerID INT NOT NULL
);
INSERT INTO dbo.SyntheticLookupThenAggregateSource (Country, CustomerID) VALUES
    (N'Canada', 1),
    (N'Canada', 2),
    (N'India', 3),
    (N'Atlantis', 4);

DROP TABLE IF EXISTS dbo.SyntheticLookupThenAggregateReference;
CREATE TABLE dbo.SyntheticLookupThenAggregateReference (
    CountryName NVARCHAR(50) NOT NULL,
    Region NVARCHAR(50) NOT NULL
);
INSERT INTO dbo.SyntheticLookupThenAggregateReference (CountryName, Region) VALUES
    (N'Canada', N'North America'),
    (N'India', N'Asia');

DROP TABLE IF EXISTS dbo.SyntheticLookupThenAggregateTarget;
CREATE TABLE dbo.SyntheticLookupThenAggregateTarget (
    Region NVARCHAR(50) NOT NULL,
    CustomerCount BIGINT NOT NULL
);
