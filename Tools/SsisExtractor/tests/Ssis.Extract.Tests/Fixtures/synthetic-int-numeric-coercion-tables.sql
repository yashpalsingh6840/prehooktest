-- Backing tables for SyntheticIntNumericCoercion.dtsx -- a PROBE, built 2026-08-30 (real
-- evidence: RBC_Demo_ETL's own Excel Source r8-to-i4 pairing and Package_Exports' own
-- wstr-to-i4 pairing were both already measured and closed in earlier rounds; i8-to-i4,
-- numeric-to-i4, and r4-to-i4 have no real evidenced instance anywhere in the tracked
-- portfolio, but were named as candidates worth covering. Seeded with midpoint values chosen
-- specifically to distinguish rounding rules for the two float-shaped pairings (numeric, r4):
-- exact values, ordinary non-midpoint values in both directions, and midpoint values on both
-- an odd and an even target either side of zero.
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticIntNumericCoercionSource;
CREATE TABLE dbo.SyntheticIntNumericCoercionSource (
    ID INT NOT NULL,
    I8Val BIGINT NOT NULL,
    NumericVal DECIMAL(18,2) NOT NULL,
    R4Val REAL NOT NULL
);
INSERT INTO dbo.SyntheticIntNumericCoercionSource (ID, I8Val, NumericVal, R4Val) VALUES
    (1, 42, 3.0, 3.0),
    (2, 100, 3.2, 3.2),
    (3, 200, 3.5, 3.5),
    (4, 300, 3.7, 3.7),
    (5, -50, -3.2, -3.2),
    (6, -60, -3.5, -3.5),
    (7, -70, -3.7, -3.7),
    (8, 400, 2.5, 2.5),
    (9, 500, 4.5, 4.5);

DROP TABLE IF EXISTS dbo.SyntheticIntNumericCoercionTarget;
CREATE TABLE dbo.SyntheticIntNumericCoercionTarget (
    ID INT NOT NULL,
    I8Val INT NOT NULL,
    NumericVal INT NOT NULL,
    R4Val INT NOT NULL
);
