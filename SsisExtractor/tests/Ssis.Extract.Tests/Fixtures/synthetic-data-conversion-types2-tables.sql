-- Backing tables for SyntheticDataConversionTypes2.dtsx -- a PROBE, built speculatively
-- 2026-08-30 (DT_I2/DT_I8/DT_DBTIMESTAMP have no real evidenced instance anywhere in the tracked
-- portfolio; DT_I4/DT_DBDATE/DT_R8/DT_WSTR/DT_BOOL are already closed). Same seed shape as the
-- original data-conversion-types probe: a valid value, a whitespace-padded valid value, invalid
-- text, an empty string, and NULL -- to confirm IgnoreFailure nulls out on failure the same way
-- as every other already-measured target type (not a truncation-on-overflow story the way
-- DT_WSTR's own measured semantics turned out to be).
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticDataConversionTypes2Input;
CREATE TABLE dbo.SyntheticDataConversionTypes2Input (
    ID INT NOT NULL,
    I2Text NVARCHAR(50) NULL,
    I8Text NVARCHAR(50) NULL,
    TimestampText NVARCHAR(50) NULL
);
INSERT INTO dbo.SyntheticDataConversionTypes2Input (ID, I2Text, I8Text, TimestampText) VALUES
    (1, N'123', N'123456789012', N'2026-01-15 10:30:00'),
    (2, N' 456 ', N' 987654321098 ', N' 2026-02-20 14:45:30 '),
    (3, N'not-a-number', N'not-a-number', N'not-a-date'),
    (4, N'', N'', N''),
    (5, NULL, NULL, NULL);

DROP TABLE IF EXISTS dbo.SyntheticDataConversionTypes2Target;
CREATE TABLE dbo.SyntheticDataConversionTypes2Target (
    ID INT NOT NULL,
    Val_i2 SMALLINT NULL,
    Val_i8 BIGINT NULL,
    Val_dt DATETIME NULL
);
