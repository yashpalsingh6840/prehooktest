-- Backing tables for SyntheticAggregate.dtsx -- built speculatively 2026-08-30 (RBC_Demo_ETL's
-- own real Microsoft.Aggregate instance, AGG_ByRegion, sits downstream of a Lookup that already
-- blocks the whole flow regardless of Aggregate support -- confirmed twice by re-surveying the
-- portfolio. Built anyway on the user's own explicit request, for completeness rather than to
-- close a real gap).
--
-- Seed data deliberately includes one NULL CustomerID, specifically to distinguish real
-- AggregationType=1 (Count) semantics: does it behave like SQL COUNT(column) (excludes NULL) or
-- COUNT(*) (includes it)? East has 3 rows but only 2 non-NULL CustomerIDs.
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticAggregateSource;
CREATE TABLE dbo.SyntheticAggregateSource (
    Region NVARCHAR(50) NOT NULL,
    CustomerID NVARCHAR(50) NULL
);
INSERT INTO dbo.SyntheticAggregateSource (Region, CustomerID) VALUES
    (N'East', N'C1'),
    (N'East', N'C2'),
    (N'East', NULL),
    (N'West', N'C3');

DROP TABLE IF EXISTS dbo.SyntheticAggregateTarget;
CREATE TABLE dbo.SyntheticAggregateTarget (
    Region NVARCHAR(50) NOT NULL,
    CustomerCount BIGINT NOT NULL
);
