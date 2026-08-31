-- Backing tables for SyntheticFailureHandler.dtsx -- Failure precedence constraints.
--
-- SyntheticFailureHandlerControl.ShouldFail is what makes the SUCCESS and FAILURE runs the same
-- fixture with one row flipped, rather than two fixtures differing in more than one way.
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticFailureHandlerTarget;
CREATE TABLE dbo.SyntheticFailureHandlerTarget (
    ID    INT NOT NULL PRIMARY KEY,
    Name  NVARCHAR(50) NOT NULL
);
GO

DROP TABLE IF EXISTS dbo.SyntheticFailureHandlerLog;
CREATE TABLE dbo.SyntheticFailureHandlerLog (
    Id      INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Marker  NVARCHAR(50) NOT NULL
);
GO

DROP TABLE IF EXISTS dbo.SyntheticFailureHandlerControl;
CREATE TABLE dbo.SyntheticFailureHandlerControl (ShouldFail BIT NOT NULL);
INSERT dbo.SyntheticFailureHandlerControl (ShouldFail) VALUES (0);
GO
