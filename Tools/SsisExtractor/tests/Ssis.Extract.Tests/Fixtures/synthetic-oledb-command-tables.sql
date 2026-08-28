-- Backing table for SyntheticOleDbCommand.dtsx (OLE DB Source -> OLE DB Command, no
-- destination component at all -- the command itself is the flow's sink).
DROP TABLE IF EXISTS dbo.SyntheticOleDbCommandTarget;
CREATE TABLE dbo.SyntheticOleDbCommandTarget (
    CustomerID INT NOT NULL PRIMARY KEY,
    Flagged BIT NOT NULL
);

INSERT INTO dbo.SyntheticOleDbCommandTarget (CustomerID, Flagged) VALUES
    (1, 0),
    (2, 0),
    (3, 0);
