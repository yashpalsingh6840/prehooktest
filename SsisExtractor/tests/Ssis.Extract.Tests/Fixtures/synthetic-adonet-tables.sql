/*
    Synthetic fixture tables -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable (dbo.Employee/Department/
    Designation in Sql/01_CreateTarget.sql). Backs
    Fixtures/SyntheticAdoNetRoundTrip.dtsx -- ADO NET Source (SqlCommand) ->
    Derived Column (LoadedAtUtc) -> ADO NET Destination. Real-world
    motivation: RBC_Demo_ETL's Package_Exports.dtsx (DFT_AdoNetRoundTrip,
    ADO_SRC_Customers -> ADO_DST_ExportLog), discovered testing ssisx
    against a real ~30-component-type client-shaped portfolio
    (SSIS_From_Sandeep, 2026-08-27). Safe to drop/recreate any time; nothing
    else in the PoC reads or writes these tables.

    Run once against .\SQLFORPOC_2022 before regenerating the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-adonet-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticAdoNetInput;
GO
CREATE TABLE dbo.SyntheticAdoNetInput
(
    ID   INT           NOT NULL,
    Name NVARCHAR(50)  NOT NULL
);
GO
INSERT INTO dbo.SyntheticAdoNetInput (ID, Name) VALUES
    (1, 'Alice'),
    (2, 'Bob');
GO

-- ID (not OrderID) deliberately -- see synthetic-oledb-source-tables.sql's own comment for why:
-- PrimaryKeyInference's naming-convention heuristic needs "<table>ID" or the fallback "ID".
DROP TABLE IF EXISTS dbo.SyntheticAdoNetTarget;
GO
CREATE TABLE dbo.SyntheticAdoNetTarget
(
    ID          INT            NOT NULL,
    Name        NVARCHAR(50)   NOT NULL,
    LoadedAtUtc DATETIME2(3)   NOT NULL
);
GO
