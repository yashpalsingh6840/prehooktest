/*  Objects for Package_Legacy.dtsx - the "legacy production patterns" package.
    Safe to re-run.

    Two things live here that 05_supporting_objects.sql cannot provide:
      1. a SECOND DATABASE, so one package can span two of them the way production's
         FXRate_Import (DN6_wbData + DN6_SessionManagement) and RORPackage do;
      2. the target for the fixed-width import that mirrors CUVImport.
*/

------------------------------------------------------------ second database
IF DB_ID('SSISDemoCache') IS NULL
    CREATE DATABASE SSISDemoCache;
GO

USE SSISDemoCache;
GO

IF OBJECT_ID('dbo.CacheEntry', 'U') IS NOT NULL
    DROP TABLE dbo.CacheEntry;
GO
CREATE TABLE dbo.CacheEntry
(
    CacheKey   NVARCHAR(100)  NOT NULL PRIMARY KEY,
    CacheValue NVARCHAR(MAX)  NULL,
    SetAt      DATETIME       NOT NULL DEFAULT GETDATE()
);
GO

-- Named after production's dbo.Cache_Set, called by FXRate_Import to push an XML blob
-- into the session-management cache.
IF OBJECT_ID('dbo.Cache_Set', 'P') IS NOT NULL
    DROP PROCEDURE dbo.Cache_Set;
GO
CREATE PROCEDURE dbo.Cache_Set
    @Key   NVARCHAR(100),
    @Value NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.CacheEntry AS t
    USING (SELECT @Key AS k) AS s ON t.CacheKey = s.k
    WHEN MATCHED THEN
        UPDATE SET CacheValue = @Value, SetAt = GETDATE()
    WHEN NOT MATCHED THEN
        INSERT (CacheKey, CacheValue) VALUES (@Key, @Value);
END
GO

--------------------------------------------------- fixed-width import target
USE SSISDemo;
GO

-- Column widths match sample-data\CustomersFixed.txt exactly: 10 / 30 / 40 / 4.
-- The file also has a CUVHDR header line, skipped via HeaderRowsToSkip = 1.
IF OBJECT_ID('dbo.CustomerFixedImport', 'U') IS NOT NULL
    DROP TABLE dbo.CustomerFixedImport;
GO
CREATE TABLE dbo.CustomerFixedImport
(
    CustomerID  NVARCHAR(10)  NULL,
    FullName    NVARCHAR(30)  NULL,
    Email       NVARCHAR(40)  NULL,
    CountryCode NVARCHAR(4)   NULL,
    ImportedAt  DATETIME      NOT NULL DEFAULT GETDATE()
);
GO

-------------------------------------------------------- Merge transform target
IF OBJECT_ID('dbo.CustomerMerged', 'U') IS NOT NULL
    DROP TABLE dbo.CustomerMerged;
GO
CREATE TABLE dbo.CustomerMerged
(
    CustomerID INT            NULL,
    Email      NVARCHAR(200)  NULL,
    MergedAt   DATETIME       NOT NULL DEFAULT GETDATE()
);
GO

PRINT 'Legacy objects created.';
GO
SELECT DatabaseName = 'SSISDemo',      TableName = name FROM SSISDemo.sys.tables
 WHERE name IN ('CustomerFixedImport', 'CustomerMerged')
UNION ALL
SELECT DatabaseName = 'SSISDemoCache', TableName = name FROM SSISDemoCache.sys.tables
 WHERE name = 'CacheEntry'
ORDER BY DatabaseName, TableName;
GO
