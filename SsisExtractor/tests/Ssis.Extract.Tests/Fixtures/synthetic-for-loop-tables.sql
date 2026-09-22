-- Backing table for SyntheticForLoop.dtsx -- Phase 3 of the unsupported-component-types plan
-- (STOCK:FORLOOP / For Loop Container). Confirmed real from a genuine SSDT-authored package
-- (D:\PoC\SSIS_Packages_From_GitHub\ETL-SSIS-Real-Scenarios\UseCase_34\...\Package.dtsx, "For
-- Loop Container"), whose own body is a single Data Flow Task -- this fixture reproduces that
-- shape with a small, deliberately verifiable counter range (Init=1, Eval=@Part<4, Assign=
-- @Part=@Part+1 -- 3 iterations), each iteration reading a DIFFERENT, counter-named CSV file
-- (see synthetic-for-loop-files/) so the target table's own row content -- not just its row
-- count -- proves each iteration ran with the correct counter value.
USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticForLoopTarget;
CREATE TABLE dbo.SyntheticForLoopTarget (
    ID INT NOT NULL,
    Label NVARCHAR(50) NOT NULL,
    LoadedAtUtc DATETIME2 NOT NULL
);
