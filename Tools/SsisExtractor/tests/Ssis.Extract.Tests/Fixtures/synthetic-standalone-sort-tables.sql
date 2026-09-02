-- Backing table for SyntheticStandaloneSort.dtsx -- gap-audit Phase 3.5 (2026-09-02). Seed rows
-- deliberately out of ID order (3, 1, 2), so Sort's own reordering effect is genuinely exercised
-- rather than coincidentally already correct.
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticStandaloneSortSource;
CREATE TABLE dbo.SyntheticStandaloneSortSource (
    ID INT NOT NULL,
    Name VARCHAR(50) NOT NULL
);

INSERT INTO dbo.SyntheticStandaloneSortSource (ID, Name) VALUES
    (3, N'Carol'),
    (1, N'Alice'),
    (2, N'Bob');
