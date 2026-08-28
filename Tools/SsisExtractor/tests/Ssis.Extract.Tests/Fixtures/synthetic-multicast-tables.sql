-- Backing tables for SyntheticMulticast.dtsx (OLE DB Source -> Multicast -> {OLE DB
-- Destination, Flat File Destination}).
DROP TABLE IF EXISTS dbo.SyntheticMulticastSource;
CREATE TABLE dbo.SyntheticMulticastSource (
    ID INT NOT NULL,
    Name VARCHAR(50) NOT NULL
);

INSERT INTO dbo.SyntheticMulticastSource (ID, Name) VALUES
    (1, 'Alice'),
    (2, 'Bob'),
    (3, 'Carol');

DROP TABLE IF EXISTS dbo.SyntheticMulticastTarget;
CREATE TABLE dbo.SyntheticMulticastTarget (
    ID INT NOT NULL,
    Name VARCHAR(50) NOT NULL
);
