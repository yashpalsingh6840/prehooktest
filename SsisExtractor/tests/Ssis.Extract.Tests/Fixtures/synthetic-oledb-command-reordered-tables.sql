-- Backing table for SyntheticOleDbCommandReordered.dtsx (gap-audit Phase 3.1: OLE DB Command
-- with two parameters, deliberately attached in the OPPOSITE order from their own '?'
-- placeholder position -- proves ResolveOleDbCommand resolves TRUE binding order from each
-- input column's own externalMetadataColumnId (Param_N), not <inputColumns> declaration
-- order, which a live SSIS object-model probe showed can legitimately diverge).
--
-- StatusCode is seeded at 0 and the flow's own source computes NewStatus = CustomerID + 100
-- (101-103), a range that never overlaps a real CustomerID (1-3). If codegen ever regresses to
-- binding by declaration order, the generated UPDATE's WHERE clause would compare CustomerID to
-- a NewStatus value instead, matching zero rows -- StatusCode would stay 0 for every row. Correct
-- binding lands StatusCode = CustomerID + 100 for every row instead. An unambiguous, easily
-- read-back signal either way.
DROP TABLE IF EXISTS dbo.SyntheticOleDbCommandReorderedTarget;
CREATE TABLE dbo.SyntheticOleDbCommandReorderedTarget (
    CustomerID INT NOT NULL PRIMARY KEY,
    StatusCode INT NOT NULL
);

INSERT INTO dbo.SyntheticOleDbCommandReorderedTarget (CustomerID, StatusCode) VALUES
    (1, 0),
    (2, 0),
    (3, 0);
