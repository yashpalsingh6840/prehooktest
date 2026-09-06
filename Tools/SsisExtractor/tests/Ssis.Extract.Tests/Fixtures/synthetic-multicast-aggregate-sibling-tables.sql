-- Backing tables for SyntheticMulticastAggregateSibling.dtsx -- built 2026-09-06, proving the
-- PackageGenerator fix for a real, previously-silent correctness bug: a plain Multicast with
-- one branch straight to a destination and a SECOND branch through an Aggregate to a DIFFERENT
-- destination, with no Lookup anywhere, used to silently drop the straight branch and wire the
-- Aggregate against the wrong destination's schema (a build-breaking CS1061 reported as a
-- fully-generatable package). Now correctly reported as a named, non-blocking-vs-blocking gap
-- instead -- this fixture exists to prove that, not to prove a working generated run (there is
-- none; the shape is deliberately unsupported).
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticMulticastAggSiblingSource;
CREATE TABLE dbo.SyntheticMulticastAggSiblingSource (
    ID INT NOT NULL,
    Region NVARCHAR(50) NOT NULL,
    CustomerID INT NULL
);
INSERT INTO dbo.SyntheticMulticastAggSiblingSource (ID, Region, CustomerID) VALUES
    (1, N'East', 101),
    (2, N'East', 102),
    (3, N'West', 103);

DROP TABLE IF EXISTS dbo.SyntheticMulticastAggSiblingTargetA;
CREATE TABLE dbo.SyntheticMulticastAggSiblingTargetA (
    ID INT NOT NULL,
    Region NVARCHAR(50) NOT NULL,
    CustomerID INT NULL
);

DROP TABLE IF EXISTS dbo.SyntheticMulticastAggSiblingTargetB;
CREATE TABLE dbo.SyntheticMulticastAggSiblingTargetB (
    Region NVARCHAR(50) NOT NULL,
    CustomerCount INT NOT NULL
);
