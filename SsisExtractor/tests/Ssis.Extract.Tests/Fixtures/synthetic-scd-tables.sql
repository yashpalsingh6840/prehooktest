-- Backing tables for SyntheticScdProbe.dtsx / SyntheticScd.dtsx (Phase 7 of the
-- unsupported-component-types plan -- Microsoft.SCD, "Slowly Changing Dimension").
--
-- Run against .\SQLFORPOC_2022 / SsisPoC BEFORE running ssis-fixture-builder, and again
-- before each measurement run (the fixture builder's own OLE DB Destination
-- ReinitializeMetaData resolves external metadata against these live tables, and both
-- fixtures mutate the dimension, so a repeat run needs the seed restored).
--
-- Deliberately mirrors the ONE real evidenced SCD package
-- (ETL-SSIS-Real-Scenarios/SCD SSIS's own SCD.dtsx: Emp_Source -> SCD -> dbo.DimEmployee)
-- shape for shape -- same four columns, same business key, same current-row marker pair --
-- with one source row per classification the component can produce, so a single dtexec run
-- reports the complete routing table rather than one rule at a time.

USE SsisPoC;
GO

IF OBJECT_ID('dbo.SyntheticScdSource', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdSource;
IF OBJECT_ID('dbo.SyntheticScdDim', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdDim;
IF OBJECT_ID('dbo.SyntheticScdOutUnchanged', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdOutUnchanged;
IF OBJECT_ID('dbo.SyntheticScdOutNew', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdOutNew;
IF OBJECT_ID('dbo.SyntheticScdOutFixed', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdOutFixed;
IF OBJECT_ID('dbo.SyntheticScdOutChanging', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdOutChanging;
IF OBJECT_ID('dbo.SyntheticScdOutHistorical', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdOutHistorical;
IF OBJECT_ID('dbo.SyntheticScdOutInferred', 'U') IS NOT NULL DROP TABLE dbo.SyntheticScdOutInferred;
GO

CREATE TABLE dbo.SyntheticScdSource
(
    EmpId       INT          NOT NULL,
    FirstName   VARCHAR(50)  NOT NULL,
    LastName    VARCHAR(50)  NOT NULL,
    Designation VARCHAR(50)  NOT NULL
);
GO

-- StartDate/EndDate are the CurrentRowWhere pair: a row is "current" when it has a StartDate
-- and no EndDate. Neither is an SCD input column -- they exist only for that filter, exactly
-- as in the real evidenced package.
CREATE TABLE dbo.SyntheticScdDim
(
    Id          INT IDENTITY(1,1) NOT NULL,
    EmpId       INT          NOT NULL,
    FirstName   VARCHAR(50)  NOT NULL,
    LastName    VARCHAR(50)  NOT NULL,
    Designation VARCHAR(50)  NOT NULL,
    StartDate   DATETIME     NULL,
    EndDate     DATETIME     NULL
);
GO

-- One observation table per SCD output. Wiring every output to its own destination is what
-- turns a single dtexec run into the component's whole routing table: whichever table a row
-- lands in IS the output it was routed to, read back directly rather than inferred.
CREATE TABLE dbo.SyntheticScdOutUnchanged  (EmpId INT NOT NULL, FirstName VARCHAR(50) NOT NULL, LastName VARCHAR(50) NOT NULL, Designation VARCHAR(50) NOT NULL);
CREATE TABLE dbo.SyntheticScdOutNew        (EmpId INT NOT NULL, FirstName VARCHAR(50) NOT NULL, LastName VARCHAR(50) NOT NULL, Designation VARCHAR(50) NOT NULL);
CREATE TABLE dbo.SyntheticScdOutFixed      (EmpId INT NOT NULL, FirstName VARCHAR(50) NOT NULL, LastName VARCHAR(50) NOT NULL, Designation VARCHAR(50) NOT NULL);
CREATE TABLE dbo.SyntheticScdOutChanging   (EmpId INT NOT NULL, FirstName VARCHAR(50) NOT NULL, LastName VARCHAR(50) NOT NULL, Designation VARCHAR(50) NOT NULL);
CREATE TABLE dbo.SyntheticScdOutHistorical (EmpId INT NOT NULL, FirstName VARCHAR(50) NOT NULL, LastName VARCHAR(50) NOT NULL, Designation VARCHAR(50) NOT NULL);
CREATE TABLE dbo.SyntheticScdOutInferred   (EmpId INT NOT NULL, FirstName VARCHAR(50) NOT NULL, LastName VARCHAR(50) NOT NULL, Designation VARCHAR(50) NOT NULL);
GO

-- ---------------------------------------------------------------------------------------
-- Seed. One source row per classification, matched against a dimension seeded to produce it.
--
--   100  unchanged                    -- every attribute identical to the current dim row
--   101  Designation changed          -- ColumnType 3 (the real package's HISTORICAL column)
--   102  FirstName changed            -- ColumnType 4 (the real package's FIXED column)
--   103  no dim row at all            -- genuinely new business key
--   104  LastName changed             -- ColumnType 2 (the real package's CHANGING column)
--   105  LastName AND Designation     -- both at once: measures how a row that qualifies for
--                                        exclusionGroup 1 AND exclusionGroup 2 is routed
--   106  dim row exists but EXPIRED   -- measures whether CurrentRowWhere is really applied
--   107  FirstName AND Designation    -- fixed + historical at once: measures precedence
--   108  FirstName AND LastName       -- fixed + changing at once: measures precedence
--   109  LastName differs by CASE only     ('Fahmy' vs 'FAHMY') -- ordinal vs. SQL collation
--   110  LastName differs by TRAILING SPACE ('Aziz' vs 'Aziz ') -- ordinal vs. SQL collation
-- ---------------------------------------------------------------------------------------

INSERT dbo.SyntheticScdDim (EmpId, FirstName, LastName, Designation, StartDate, EndDate) VALUES
    (100, 'Ahmed',   'Mohamed', 'Software Engineer', '2020-01-01', NULL),
    (101, 'Karim',   'Mahmoud', 'DBA',               '2020-01-01', NULL),
    (102, 'Ali',     'Omar',    'Software Engineer', '2020-01-01', NULL),
    (104, 'Jamal',   'Eslam',   'IT Head',           '2020-01-01', NULL),
    (105, 'Sara',    'Ali',     'Analyst',           '2020-01-01', NULL),
    (106, 'Omar',    'Zaid',    'Clerk',             '2019-01-01', '2020-01-01'),
    (107, 'Hesham',  'Nabil',   'Support',           '2020-01-01', NULL),
    (108, 'Rania',   'Adel',    'QA',                '2020-01-01', NULL),
    (109, 'Yasser',  'Fahmy',   'Tester',            '2020-01-01', NULL),
    (110, 'Mostafa', 'Aziz',    'Coordinator',       '2020-01-01', NULL);
GO

INSERT dbo.SyntheticScdSource (EmpId, FirstName, LastName, Designation) VALUES
    (100, 'Ahmed',   'Mohamed', 'Software Engineer'),
    (101, 'Karim',   'Mahmoud', 'Senior DBA'),
    (102, 'Nael',    'Omar',    'Software Engineer'),
    (103, 'Raed',    'Kamal',   'Software Engineer'),
    (104, 'Jamal',   'Badr',    'IT Head'),
    (105, 'Sara',    'Badr',    'Senior Analyst'),
    (106, 'Omar',    'Zaid',    'Clerk'),
    (107, 'Waleed',  'Nabil',   'Team Lead'),
    (108, 'Salma',   'Farouk',  'QA'),
    (109, 'Yasser',  'FAHMY',   'Tester'),
    (110, 'Mostafa', 'Aziz ',   'Coordinator');
GO
