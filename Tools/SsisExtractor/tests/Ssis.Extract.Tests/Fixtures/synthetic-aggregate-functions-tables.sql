-- Backing tables for SyntheticAggregateFunctions.dtsx -- built 2026-09-02 (gap-audit Phase 3.3),
-- closing the widened Aggregate support (Sum/Average/Minimum/Maximum/CountDistinct/CountAll)
-- beyond the original GroupBy/Count-only round.
--
-- Seed data deliberately reproduces the exact distinguishing shape used to MEASURE the real raw
-- AggregationType values via a live dtexec probe (Ssis.Extract.FixtureBuilder's own
-- aggregate-type-semantics-probe, since removed): a group with a NULL, a duplicate value, and a
-- wide spread (Region A) so Sum/Average/Minimum/Maximum/CountDistinct/CountAll can never
-- coincidentally collide; a plain group (Region B); and a group that is ALL NULL (Region C),
-- specifically to prove Sum/Average/Minimum/Maximum return NULL, not 0, when nothing real
-- contributes -- the one case where .NET's own Enumerable.Sum diverges from SSIS/SQL semantics
-- (Sum returns 0 for an all-null nullable sequence; Average/Min/Max already return null).
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticAggregateFunctionsSource;
CREATE TABLE dbo.SyntheticAggregateFunctionsSource (
    Region NVARCHAR(50) NOT NULL,
    Amount FLOAT NULL
);
INSERT INTO dbo.SyntheticAggregateFunctionsSource (Region, Amount) VALUES
    (N'A', 10),
    (N'A', 10),
    (N'A', 20),
    (N'A', NULL),
    (N'B', 5),
    (N'B', 5),
    (N'B', 5),
    (N'C', NULL),
    (N'C', NULL);

DROP TABLE IF EXISTS dbo.SyntheticAggregateFunctionsTarget;
CREATE TABLE dbo.SyntheticAggregateFunctionsTarget (
    Region NVARCHAR(50) NOT NULL,
    TotalRows BIGINT NOT NULL,
    DistinctAmounts BIGINT NOT NULL,
    SumAmount FLOAT NULL,
    AverageAmount FLOAT NULL,
    MinAmount FLOAT NULL,
    MaxAmount FLOAT NULL
);
