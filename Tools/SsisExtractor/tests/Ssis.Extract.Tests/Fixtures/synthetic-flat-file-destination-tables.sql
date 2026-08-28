/*
    Synthetic fixture table -- extractor component-coverage test data only.
    -------------------------------------------------------------------------
    NOT part of the SSIS PoC's own deliverable. Backs a small standalone package
    (Fixtures/SyntheticFlatFileDestination.dtsx) built to prove Microsoft.FlatFileDestination
    support end to end, added 2026-08-28 -- the gap discovered testing ssisx against a real
    third-party portfolio (SSIS_From_Sandeep)'s Package_Exports.dtsx (DFT_ExportDelimited/
    DFT_ExportFixedWidth: an OLE DB Source over dbo.StagingCustomers written to a Delimited and
    a FixedWidth flat file, both direct-copy pipelines with no Derived Column at all).

    ID=3's FullName is deliberately 22 characters -- longer than the FixedWidth connection
    manager's own 15-character column width for FullName -- to exercise real truncation
    behavior end to end, not an assumption about it.

    Run once against .\SQLFORPOC_2022 before generating/executing the fixture:
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Tools\SsisExtractor\tests\Ssis.Extract.Tests\Fixtures\synthetic-flat-file-destination-tables.sql
*/

USE SsisPoC;
GO

DROP TABLE IF EXISTS dbo.SyntheticFlatFileDestinationInput;
GO
CREATE TABLE dbo.SyntheticFlatFileDestinationInput
(
    CustomerID NVARCHAR(20)  NOT NULL,
    FullName   NVARCHAR(200) NOT NULL,
    CleanEmail NVARCHAR(200) NOT NULL
);
GO
INSERT INTO dbo.SyntheticFlatFileDestinationInput (CustomerID, FullName, CleanEmail) VALUES
    (N'C001', N'Alice Smith',            N'alice@example.com'),
    (N'C002', N'Bob',                    N'bob@example.com'),
    (N'C003', N'Charlotte Fitzgeraldson', N'charlotte@example.com'); -- FullName > 15 chars: truncation case
GO

-- A third, ordinary OLE DB Source -> Derived Column -> OLE DB Destination flow, matching
-- RBC_Demo_ETL's own Package_Exports.dtsx real shape (which ALSO has a SQL destination,
-- DFT_AdoNetRoundTrip's ADO_DST_ExportLog) -- this is what gives the generated package a real
-- SQL table for AddEtlDbContext<T>()/IUnitOfWork's transaction to wrap, exactly like every real
-- evidenced package. A package whose ONLY flows are Flat File Destinations (zero SQL tables at
-- all) is a separate, unevidenced, out-of-scope shape -- not what this fixture is proving.
-- ID (not a compound key) deliberately -- see synthetic-conditional-split-tables.sql's own
-- comment for why: PrimaryKeyInference's naming-convention heuristic needs "<table>ID" or "ID".
DROP TABLE IF EXISTS dbo.SyntheticFlatFileDestinationLog;
GO
CREATE TABLE dbo.SyntheticFlatFileDestinationLog
(
    ID          INT          NOT NULL,
    Note        NVARCHAR(50) NOT NULL,
    LoadedAtUtc DATETIME2    NOT NULL
);
GO
DROP TABLE IF EXISTS dbo.SyntheticFlatFileDestinationLogSource;
GO
CREATE TABLE dbo.SyntheticFlatFileDestinationLogSource
(
    ID   INT          NOT NULL,
    Note NVARCHAR(50) NOT NULL
);
GO
INSERT INTO dbo.SyntheticFlatFileDestinationLogSource (ID, Note) VALUES
    (1, N'export run started');
GO
