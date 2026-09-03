-- Backing tables for SyntheticMulticastDiscard.dtsx -- built 2026-09-02, proving
-- GenerateMulticastFlow's own defensive discard-skip in isolation from the Lookup+Aggregate
-- composed shape (a plain Multicast with one live branch and one discarded RowCount branch,
-- no Lookup and no Aggregate anywhere).
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticMulticastDiscardSource;
CREATE TABLE dbo.SyntheticMulticastDiscardSource (
    ID INT NOT NULL,
    Name NVARCHAR(50) NOT NULL
);
INSERT INTO dbo.SyntheticMulticastDiscardSource (ID, Name) VALUES
    (1, N'Alice'),
    (2, N'Bob');

DROP TABLE IF EXISTS dbo.SyntheticMulticastDiscardTarget;
CREATE TABLE dbo.SyntheticMulticastDiscardTarget (
    ID INT NOT NULL,
    Name NVARCHAR(50) NOT NULL
);
