-- Backing tables for SyntheticCondGuard.dtsx -- conditional precedence constraint guards.
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticCondGuardTarget;
CREATE TABLE dbo.SyntheticCondGuardTarget (
    ID    INT NOT NULL PRIMARY KEY,
    Name  NVARCHAR(50) NOT NULL
);
GO

DROP TABLE IF EXISTS dbo.SyntheticCondGuardLog;
CREATE TABLE dbo.SyntheticCondGuardLog (
    Id      INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Marker  NVARCHAR(50) NOT NULL
);
GO
