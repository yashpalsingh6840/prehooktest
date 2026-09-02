/*  Supporting objects for the component-showcase packages.
    Safe to re-run - every object is dropped and recreated.

    These exist so the added SSIS components have something real to work against:
    a lookup reference table, an error sink, an ADO.NET target, and a run log that
    the OnError event handler writes to.                                            */

USE SSISDemo;
GO

------------------------------------------------------------------- error sink
-- Destination for rows redirected down an OLE DB Destination's error output.
-- Mirrors dbo.adMutualFund_InvalidRecords in the production MFDBUpdate package.
IF OBJECT_ID('dbo.StagingCustomers_Errors', 'U') IS NOT NULL
    DROP TABLE dbo.StagingCustomers_Errors;
GO
CREATE TABLE dbo.StagingCustomers_Errors
(
    ErrorRowID      INT IDENTITY(1,1) PRIMARY KEY,
    CustomerID      NVARCHAR(50)  NULL,
    FirstName       NVARCHAR(100) NULL,
    LastName        NVARCHAR(100) NULL,
    Email           NVARCHAR(200) NULL,
    ErrorCode       INT           NULL,
    ErrorColumn     INT           NULL,
    FailedAt        DATETIME      NOT NULL DEFAULT GETDATE()
);
GO

--------------------------------------------------------------- lookup source
-- Reference table for the Lookup transform. Keyed on the Country value exactly
-- as it appears in sample-data\Customers.csv (full names, not ISO codes).
--
-- NOTE: 'Czech Republic' and 'Spain' are deliberately ABSENT so that two rows
-- miss the lookup. That exercises the Lookup's no-match output instead of only
-- the happy path - the same shape as production's PN6PinkSheetDN / ETAccSSIS.
IF OBJECT_ID('dbo.CountryLookup', 'U') IS NOT NULL
    DROP TABLE dbo.CountryLookup;
GO
CREATE TABLE dbo.CountryLookup
(
    CountryName NVARCHAR(100) NOT NULL PRIMARY KEY,
    CountryCode NVARCHAR(10)  NOT NULL,
    Region      NVARCHAR(50)  NOT NULL
);
GO
INSERT INTO dbo.CountryLookup (CountryName, CountryCode, Region) VALUES
    ('USA',       'US', 'North America'),
    ('Canada',    'CA', 'North America'),
    ('Mexico',    'MX', 'North America'),
    ('UAE',       'AE', 'Middle East'),
    ('Ireland',   'IE', 'Europe'),
    ('UK',        'GB', 'Europe'),
    ('Singapore', 'SG', 'Asia Pacific');
GO

----------------------------------------------------- transform showcase target
-- Target for Package_Transforms: one row per customer after the stock
-- transforms have run (derived, converted, split, looked up, sorted).
IF OBJECT_ID('dbo.CustomerEnriched', 'U') IS NOT NULL
    DROP TABLE dbo.CustomerEnriched;
GO
CREATE TABLE dbo.CustomerEnriched
(
    -- NULLable on purpose: the Conditional Split's Invalid branch carries the row
    -- whose CustomerID is blank in the CSV, and it must still land here.
    CustomerID       INT           NULL,
    FullName         NVARCHAR(200) NULL,
    EmailDomain      NVARCHAR(200) NULL,
    CountryCode      NVARCHAR(10)  NULL,
    CountryName      NVARCHAR(100) NULL,
    Region           NVARCHAR(50)  NULL,
    SignupDate       DATE          NULL,
    TenureDays       INT           NULL,
    ContactPhone     NVARCHAR(50)  NULL,   -- arrives via Merge Join
    RowSource        NVARCHAR(30)  NULL,   -- set by Conditional Split branch
    LoadedAt         DATETIME      NOT NULL DEFAULT GETDATE()
);
GO

-- Region rollup produced by the Aggregate transform.
IF OBJECT_ID('dbo.RegionSummary', 'U') IS NOT NULL
    DROP TABLE dbo.RegionSummary;
GO
CREATE TABLE dbo.RegionSummary
(
    Region        NVARCHAR(50) NOT NULL,
    CustomerCount INT          NOT NULL,
    BuiltAt       DATETIME     NOT NULL DEFAULT GETDATE()
);
GO

------------------------------------------------------- Merge Join demo target
-- Result of joining Customers.csv to CustomerContacts.csv on CustomerID, with
-- both inputs sorted first (Merge Join requires sorted inputs).
-- CustomerContacts.csv omits customer 8 and adds a customer 11 that does not
-- exist, so an INNER join legitimately drops rows from both sides.
IF OBJECT_ID('dbo.CustomerWithContact', 'U') IS NOT NULL
    DROP TABLE dbo.CustomerWithContact;
GO
CREATE TABLE dbo.CustomerWithContact
(
    CustomerID       INT           NULL,
    Email            NVARCHAR(200) NULL,
    ContactPhone     NVARCHAR(50)  NULL,
    PreferredChannel NVARCHAR(30)  NULL,
    JoinedAt         DATETIME      NOT NULL DEFAULT GETDATE()
);
GO

--------------------------------------------------------- ADO.NET destination
-- Loaded by the ADO NET Destination in Package_Exports (production uses this
-- pattern in MODReportDaily_DI / MODReportMonthly_DI).
IF OBJECT_ID('dbo.CustomerExportLog', 'U') IS NOT NULL
    DROP TABLE dbo.CustomerExportLog;
GO
CREATE TABLE dbo.CustomerExportLog
(
    ExportID    INT IDENTITY(1,1) PRIMARY KEY,
    CustomerID  INT           NULL,
    FullName    NVARCHAR(200) NULL,
    ExportedAt  DATETIME      NOT NULL DEFAULT GETDATE()
);
GO

------------------------------------------------------- OLE DB Command target
-- Updated row-by-row by the OLE DB Command component (blocker C5 in the
-- production analysis - deliberately reproduced here so the N+1 pattern is
-- visible and measurable).
IF OBJECT_ID('dbo.CustomerFlag', 'U') IS NOT NULL
    DROP TABLE dbo.CustomerFlag;
GO
CREATE TABLE dbo.CustomerFlag
(
    CustomerID  INT          NOT NULL PRIMARY KEY,
    FlagValue   NVARCHAR(20) NULL,
    UpdatedAt   DATETIME     NULL
);
GO
INSERT INTO dbo.CustomerFlag (CustomerID, FlagValue)
SELECT DISTINCT CustomerID, 'unset' FROM dbo.Orders;
GO

IF OBJECT_ID('dbo.usp_SetCustomerFlag', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_SetCustomerFlag;
GO
CREATE PROCEDURE dbo.usp_SetCustomerFlag
    @CustomerID INT,
    @FlagValue  NVARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.CustomerFlag
       SET FlagValue = @FlagValue, UpdatedAt = GETDATE()
     WHERE CustomerID = @CustomerID;
END
GO

--------------------------------------------------------------- run/event log
-- Written by the package OnError event handler and the log provider demo.
IF OBJECT_ID('dbo.EtlRunLog', 'U') IS NOT NULL
    DROP TABLE dbo.EtlRunLog;
GO
CREATE TABLE dbo.EtlRunLog
(
    RunLogID     INT IDENTITY(1,1) PRIMARY KEY,
    PackageName  NVARCHAR(200) NULL,
    SourceName   NVARCHAR(200) NULL,
    EventType    NVARCHAR(50)  NULL,
    ErrorCode    INT           NULL,
    MessageText  NVARCHAR(MAX) NULL,
    LoggedAt     DATETIME      NOT NULL DEFAULT GETDATE()
);
GO

PRINT 'Supporting objects created.';
GO
SELECT TableName = name FROM sys.tables
 WHERE name IN ('StagingCustomers_Errors','CountryLookup','CustomerEnriched',
                'RegionSummary','CustomerExportLog','CustomerFlag','EtlRunLog')
 ORDER BY name;
GO
