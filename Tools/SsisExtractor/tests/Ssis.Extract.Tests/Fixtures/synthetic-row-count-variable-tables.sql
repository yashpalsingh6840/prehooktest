-- Backing tables for SyntheticRowCountVariable.dtsx (gap-audit Phase 3.2): an OLE DB Source
-- (a literal 3-row VALUES() list, no separate source table needed) -> RowCount
-- (User::RowsLoaded) -> OLE DB Destination, then a post-flow Script Task reads the variable
-- back and logs it -- proves both that RowCount's own count is genuinely "rows that reached
-- this component" and that a downstream step sees the TRUE final count, not a stale/default
-- one.
DROP TABLE IF EXISTS dbo.SyntheticRowCountVariableTarget;
CREATE TABLE dbo.SyntheticRowCountVariableTarget (
    ID INT NOT NULL PRIMARY KEY,
    Name NVARCHAR(50) NOT NULL
);

DROP TABLE IF EXISTS dbo.SyntheticRowCountVariableLog;
CREATE TABLE dbo.SyntheticRowCountVariableLog (
    LoggedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    RowsLoaded INT NOT NULL
);
