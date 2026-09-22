-- Backing tables for SyntheticPctSampling.dtsx (OLE DB Source -> Percentage Sampling ->
-- {OLE DB Destination (sampled), OLE DB Destination (not sampled)}).
--
-- 200 seeded rows -- large enough for a statistical check of the declared sampling percentage
-- (SamplingValue) without needing an exact per-row match, and large enough to also support the
-- real-dtexec seed-reproducibility probe this fixture was first built for (see
-- Ssis.Extract.Codegen's PctSamplingPlan/PlanPctSampling doc comment for that finding).
DROP TABLE IF EXISTS dbo.SyntheticPctSamplingSource;
CREATE TABLE dbo.SyntheticPctSamplingSource (
    ID INT NOT NULL,
    Name VARCHAR(50) NOT NULL
);

;WITH Numbers AS (
    SELECT TOP (200) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT INTO dbo.SyntheticPctSamplingSource (ID, Name)
SELECT N, CONCAT('Row', N) FROM Numbers;

DROP TABLE IF EXISTS dbo.SyntheticPctSamplingSampled;
CREATE TABLE dbo.SyntheticPctSamplingSampled (
    ID INT NOT NULL,
    Name VARCHAR(50) NOT NULL
);

DROP TABLE IF EXISTS dbo.SyntheticPctSamplingNotSampled;
CREATE TABLE dbo.SyntheticPctSamplingNotSampled (
    ID INT NOT NULL,
    Name VARCHAR(50) NOT NULL
);
