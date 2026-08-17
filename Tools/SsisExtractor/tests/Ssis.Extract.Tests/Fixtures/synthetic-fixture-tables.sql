/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). These back a small standalone
    Data Flow (Fixtures/SyntheticLookupSplit.dtsx) built purely so the
    extractor has a real, object-model-validated Lookup + Conditional Split
    pipeline to parse -- see Tools/SsisExtractor/docs/report-schema.md
    "Synthetic component-coverage fixtures". Safe to drop/recreate any time;
    nothing else in the PoC reads or writes these tables.

    Run once against .\SQLFORPOC before regenerating the fixture:
        sqlcmd -S ".\SQLFORPOC" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-fixture-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticSourceOrder;
GO
CREATE TABLE dbo.SyntheticSourceOrder
(
    OrderID    INT            NOT NULL CONSTRAINT PK_SyntheticSourceOrder PRIMARY KEY,
    CustomerID INT            NOT NULL,
    Amount     DECIMAL(18, 2) NOT NULL
);
GO

DROP TABLE IF EXISTS dbo.SyntheticCustomer;
GO
CREATE TABLE dbo.SyntheticCustomer
(
    CustomerID   INT          NOT NULL CONSTRAINT PK_SyntheticCustomer PRIMARY KEY,
    CustomerName NVARCHAR(100) NOT NULL,
    Region       NVARCHAR(50)  NOT NULL
);
GO

-- Lookup match, Conditional Split "HighValue" case (Amount > 1000)
DROP TABLE IF EXISTS dbo.SyntheticHighValue;
GO
CREATE TABLE dbo.SyntheticHighValue
(
    OrderID      INT            NOT NULL,
    CustomerID   INT            NOT NULL,
    CustomerName NVARCHAR(100)  NOT NULL,
    Amount       DECIMAL(18, 2) NOT NULL
);
GO

-- Lookup match, Conditional Split default case
DROP TABLE IF EXISTS dbo.SyntheticLowValue;
GO
CREATE TABLE dbo.SyntheticLowValue
(
    OrderID      INT            NOT NULL,
    CustomerID   INT            NOT NULL,
    CustomerName NVARCHAR(100)  NOT NULL,
    Amount       DECIMAL(18, 2) NOT NULL
);
GO

-- Lookup no-match output (redirected, not failed)
DROP TABLE IF EXISTS dbo.SyntheticNoMatch;
GO
CREATE TABLE dbo.SyntheticNoMatch
(
    OrderID    INT            NOT NULL,
    CustomerID INT            NOT NULL,
    Amount     DECIMAL(18, 2) NOT NULL
);
GO

-- Script Component fixture (Fixtures/SyntheticScriptComponent.dtsx): a synchronous
-- transform's passthrough output, same shape as its own source so the mapping is trivial.
DROP TABLE IF EXISTS dbo.SyntheticScriptOutput;
GO
CREATE TABLE dbo.SyntheticScriptOutput
(
    OrderID    INT            NOT NULL,
    CustomerID INT            NOT NULL,
    Amount     DECIMAL(18, 2) NOT NULL
);
GO
