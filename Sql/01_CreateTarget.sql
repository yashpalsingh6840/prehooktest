/*
    SSIS PoC - target database and tables
    -------------------------------------
    Run once before executing the packages:
        sqlcmd -S ".\SQLFORPOC" -E -i Sql\01_CreateTarget.sql

    LoadEmployees.dtsx writes to dbo.Employee. Four of its columns are produced
    by the Derived Column transform rather than read from the CSV:
        EmployeeKey  <- Department + EmployeeID
        FullName     <- FirstName + LastName
        Location     <- City + State
        LoadedAtUtc  <- GETUTCDATE() audit stamp

    LoadReferenceData.dtsx writes to dbo.Department and dbo.Designation from
    two CSVs in two different folders. Each also has a derived key column and
    a derived LoadedAtUtc stamp.

    Every column fed by a Derived Column output is NVARCHAR, not VARCHAR: the
    transform emits DT_WSTR, and a VARCHAR destination fails the load with a
    unicode/non-unicode conversion error.
*/

IF DB_ID('SsisPoC') IS NULL
    CREATE DATABASE SsisPoC;
GO

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.Employee;
GO

CREATE TABLE dbo.Employee
(
    EmployeeID  INT             NOT NULL CONSTRAINT PK_Employee PRIMARY KEY,
    EmployeeKey NVARCHAR(20)    NOT NULL,   -- derived
    FullName    NVARCHAR(101)   NOT NULL,   -- derived
    Department  NVARCHAR(50)    NOT NULL,
    HireDate    DATE            NOT NULL,
    Salary      DECIMAL(18, 2)  NOT NULL,
    Location    NVARCHAR(60)    NOT NULL,   -- derived
    LoadedAtUtc DATETIME2(3)    NOT NULL    -- derived
);
GO

DROP TABLE IF EXISTS dbo.Department;
GO

CREATE TABLE dbo.Department
(
    DepartmentID   INT           NOT NULL CONSTRAINT PK_Department PRIMARY KEY,
    DepartmentKey  NVARCHAR(25)  NOT NULL,  -- derived: CODE-ID
    DepartmentCode NVARCHAR(10)  NOT NULL,
    DepartmentName NVARCHAR(50)  NOT NULL,
    CostCenter     NVARCHAR(20)  NOT NULL,
    HeadCount      INT           NOT NULL,
    LoadedAtUtc    DATETIME2(3)  NOT NULL   -- derived
);
GO

DROP TABLE IF EXISTS dbo.LoadAudit;
GO

/* Written by LoadReferenceData's SQL_Audit task. The token it records comes
   from a SENSITIVE package parameter, supplied by an encrypted SSISDB
   environment variable and passed via an Execute SQL Task parameter mapping.
   Nothing outside SQL Server ever sees the value. */
CREATE TABLE dbo.LoadAudit
(
    AuditId     INT           NOT NULL IDENTITY(1,1) CONSTRAINT PK_LoadAudit PRIMARY KEY,
    LoadName    NVARCHAR(50)  NOT NULL,
    AuditToken  NVARCHAR(100) NOT NULL,
    LoadedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_LoadAudit_At DEFAULT (SYSUTCDATETIME())
);
GO

DROP TABLE IF EXISTS dbo.Designation;
GO

CREATE TABLE dbo.Designation
(
    DesignationID   INT          NOT NULL CONSTRAINT PK_Designation PRIMARY KEY,
    DesignationKey  NVARCHAR(25) NOT NULL, -- derived: CODE-LEVEL
    DesignationCode NVARCHAR(10) NOT NULL,
    Title           NVARCHAR(50) NOT NULL,
    JobLevel        INT          NOT NULL,
    Band            NVARCHAR(5)  NOT NULL,
    LoadedAtUtc     DATETIME2(3) NOT NULL  -- derived
);
GO
