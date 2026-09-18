-- Backing tables for SyntheticScdDataConversionAttribute.dtsx (gap-audit plan
-- glittery-spinning-mochi.md, 2026-09-18, Step 3 -- "SCD reading a Data-Conversion-produced
-- column").
--
-- Run against .\SQLFORPOC_2022 / SsisPoC BEFORE running ssis-fixture-builder, and again
-- before each measurement run (the fixture builder's own OLE DB Destination
-- ReinitializeMetaData resolves external metadata against these live tables, and the fixture
-- mutates neither -- both dtexec probes and the generated exe read the same seed each run).
--
-- Reproduces the real evidenced package's own shape (Sales-DataWarehouse-with-Incremental-
-- Load-SSIS-ETL-Pipeline's own dimcustomer.dtsx: a static-literal Derived Column ("ssc" <- "1")
-- feeding a Data Conversion column, "Copy of ssc", which the SCD compares as a Fixed
-- (ColumnType=4) attribute) -- with one source row per classification the component can
-- produce, so a single dtexec run reports the complete routing table rather than one rule at a
-- time.

USE SsisPoC;
GO

IF OBJECT_ID('dbo.SyntheticScdDcaSource', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdDcaSource;
IF OBJECT_ID('dbo.SyntheticScdDcaDim', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdDcaDim;
IF OBJECT_ID('dbo.SyntheticScdDcaOutUnchanged', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdDcaOutUnchanged;
IF OBJECT_ID('dbo.SyntheticScdDcaOutNew', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdDcaOutNew;
IF OBJECT_ID('dbo.SyntheticScdDcaOutFixed', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdDcaOutFixed;
IF OBJECT_ID('dbo.SyntheticScdDcaOutChanging', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdDcaOutChanging;
GO

CREATE TABLE dbo.SyntheticScdDcaSource
(
    EmpId     INT          NOT NULL,
    FirstName VARCHAR(50)  NOT NULL
);
GO

-- StartDate/EndDate are the CurrentRowWhere pair, same convention as synthetic-scd-tables.sql.
-- CodeStr is deliberately VARCHAR(5) -- the real dimension column a Data-Conversion-truncated
-- DT_STR value is compared against.
CREATE TABLE dbo.SyntheticScdDcaDim
(
    Id        INT IDENTITY(1,1) NOT NULL,
    EmpId     INT          NOT NULL,
    FirstName VARCHAR(50)  NOT NULL,
    CodeStr   VARCHAR(5)   NOT NULL,
    StartDate DATETIME     NULL,
    EndDate   DATETIME     NULL
);
GO

-- One observation table per SCD output this fixture actually wires (Historical/Inferred Member
-- are left unwired -- nothing here exercises either).
CREATE TABLE dbo.SyntheticScdDcaOutUnchanged (EmpId INT NOT NULL, FirstName VARCHAR(50) NOT NULL, CodeStr VARCHAR(5) NOT NULL);
CREATE TABLE dbo.SyntheticScdDcaOutNew       (EmpId INT NOT NULL, FirstName VARCHAR(50) NOT NULL, CodeStr VARCHAR(5) NOT NULL);
CREATE TABLE dbo.SyntheticScdDcaOutFixed     (EmpId INT NOT NULL, FirstName VARCHAR(50) NOT NULL, CodeStr VARCHAR(5) NOT NULL);
CREATE TABLE dbo.SyntheticScdDcaOutChanging  (EmpId INT NOT NULL, FirstName VARCHAR(50) NOT NULL, CodeStr VARCHAR(5) NOT NULL);
GO

-- ---------------------------------------------------------------------------------------
-- Seed. One source row per classification, matched against a dimension seeded to produce it.
-- The Data-Conversion attribute is CodeStr <- (DT_STR,5) "HELLO WORLD" -- a STATIC LITERAL,
-- identical for EVERY incoming row (matching the real evidenced package's own static-literal
-- Derived Column shape exactly) -- so classification varies purely by what the DIMENSION's own
-- CodeStr already holds, not by anything computed per source row.
--
--   400  Alice, dimension CodeStr='HELLO' (matches the computed "HELLO"), FirstName unchanged
--        -> Unchanged
--   401  Bob,   dimension CodeStr='WORLD' (DIFFERENT from the computed "HELLO"), FirstName
--        unchanged -> Fixed Attribute Output
--   402  Carol -- no dimension row at all -> New Output
--   403  David, dimension CodeStr='HELLO' (matches), but FirstName changed ("Dave" -> "David")
--        -> Changing Attribute Updates Output
-- ---------------------------------------------------------------------------------------

INSERT dbo.SyntheticScdDcaDim (EmpId, FirstName, CodeStr, StartDate, EndDate) VALUES
    (400, 'Alice', 'HELLO', '2020-01-01', NULL),
    (401, 'Bob',   'WORLD', '2020-01-01', NULL),
    (403, 'Dave',  'HELLO', '2020-01-01', NULL);
GO

INSERT dbo.SyntheticScdDcaSource (EmpId, FirstName) VALUES
    (400, 'Alice'),
    (401, 'Bob'),
    (402, 'Carol'),
    (403, 'David');
GO
