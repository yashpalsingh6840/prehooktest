/*
    SSIS PoC - SQL login for LoadEmployees' target connection
    -----------------------------------------------------------
    Run once (or after rotating the password -- see below):
        sqlcmd -S ".\SQLFORPOC_2022" -E -i Sql\04_CreateLoaderCredential.sql

    Why this exists: every other connection in this PoC uses Windows/
    Integrated Security, which has no password to protect -- that is exactly
    why TargetConnectionString could never be marked Sensitive (see trap 13
    in CLAUDE.md) and why AuditToken, the one sensitive value in the catalog
    before this script, demonstrates encryption-at-rest but is deliberately
    left unbound: nothing in either package could legally consume it.

    A connection manager's Password IS a legal consumer (per trap 13, the
    only one SSIS allows). So CM_TargetDb in LoadEmployees.dtsx was switched
    from Integrated Security to SQL Server Authentication, using this login,
    specifically to give the catalog's sensitive-value machinery something
    real to protect end to end. LoadReferenceData is untouched -- this is
    the "at least one package" version of trap 13's closing gap, not a
    project-wide credential rework.

    SQL Server here already runs in mixed mode (verified via
    SERVERPROPERTY('IsIntegratedSecurityOnly') = 0), so no auth-mode change
    or service restart was needed -- just the login itself.

    ROTATING THE PASSWORD: change it here AND in the SSISDB environment
    variable that Sql\02_SetupCatalogEnvironment.sql binds to
    CM.CM_TargetDb.Password (see that script's TargetDbPassword section).
    The two must always match -- this script sets the real login secret,
    the environment variable is what the running package receives; SSIS
    never lets a package read either value back out (Password is a
    write-only property on the connection manager, by design).
*/

USE master;
GO

IF SUSER_ID('ssis_loader') IS NULL
    CREATE LOGIN ssis_loader
        WITH PASSWORD = N'ZMNRgL4kUp5iyHi+*wjhuMRer-6*',
             CHECK_POLICY = ON,
             CHECK_EXPIRATION = OFF;
GO

USE SsisPoC;
GO

IF DATABASE_PRINCIPAL_ID('ssis_loader') IS NULL
    CREATE USER ssis_loader FOR LOGIN ssis_loader;
GO

-- LoadEmployees' OLE DB Destination inserts rows via fast load
-- (IRowsetFastLoad), which on SQL Server 2016+ needs no bulkadmin/ALTER
-- TABLE -- but opening the fastload rowset also resolves column metadata,
-- which needs SELECT too. INSERT-only was tried first and failed validation
-- with "The SELECT permission was denied on the object 'Employee'" --
-- verified empirically, not assumed up front.
GRANT SELECT, INSERT ON dbo.Employee TO ssis_loader;

-- The package's SQL_TruncateTarget task runs TRUNCATE TABLE dbo.Employee
-- through the same connection manager before loading. TRUNCATE requires
-- ALTER TABLE, not just DELETE -- SELECT+INSERT alone failed with "Cannot
-- find the object 'Employee'" (SQL Server's deliberately vague message for
-- "exists but you lack permission"). Verified empirically after the first
-- retry still failed.
GRANT ALTER ON dbo.Employee TO ssis_loader;
GO

PRINT 'ssis_loader login/user ready; SELECT, INSERT, ALTER granted on dbo.Employee.';
