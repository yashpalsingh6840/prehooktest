-- Backing tables for SyntheticCondConstraint.dtsx -- the conditional-precedence-constraint probe.
--
-- SyntheticCondConstraintControl.ShouldFail is what makes the SUCCESS and FAILURE runs the same
-- fixture with one row flipped, rather than two fixtures differing in more than one way.
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticCondConstraintLog;
CREATE TABLE dbo.SyntheticCondConstraintLog (
    Id      INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Marker  NVARCHAR(50) NOT NULL
);
GO

DROP TABLE IF EXISTS dbo.SyntheticCondConstraintControl;
CREATE TABLE dbo.SyntheticCondConstraintControl (ShouldFail BIT NOT NULL);
INSERT dbo.SyntheticCondConstraintControl (ShouldFail) VALUES (0);
GO
