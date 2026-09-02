/*
    Verification harness - reproduces in pure T-SQL exactly what the SSIS package does,
    so the ETL logic can be validated independently of the SSIS runtime.

    Mirrors:
      SQL_TruncateTargets      -> the TRUNCATE statements below
      DFT_LoadCustomers        -> the INSERT into dbo.StagingCustomers
                                  (including the row-level cleansing the Script
                                   Component performs: TRIM, LOWER(email), FullName,
                                   IsValidRow flag, LoadDateTime)
      DFT_BuildSalesSummary    -> the INSERT into dbo.SalesSummary
                                  (including the CustomerTier the Script Component
                                   derives from TotalRevenue)

    Values come from sample-data\Customers.csv verbatim, whitespace included.
*/
USE SSISDemo;
GO

TRUNCATE TABLE dbo.StagingCustomers;
TRUNCATE TABLE dbo.SalesSummary;
GO

-- ---- DFT_LoadCustomers : raw CSV rows exactly as they appear in the file ----
DECLARE @csv TABLE
(
    CustomerID  NVARCHAR(20),
    FirstName   NVARCHAR(100),
    LastName    NVARCHAR(100),
    Email       NVARCHAR(200),
    Phone       NVARCHAR(50),
    SignupDate  NVARCHAR(50),
    Country     NVARCHAR(100)
);

INSERT INTO @csv VALUES
 (N'1',  N'John',    N'Smith',  N'john.smith@example.com',      N'555-0101', N'2021-03-15', N'USA'),
 (N'2',  N'Priya',   N'Patel',  N' PRIYA.PATEL@example.com ',   N'555-0102', N'2020-11-02', N'Canada'),
 (N'3',  N'Wei',     N'Zhang',  N'wei.zhang@example.com',       N'555-0103', N'2022-01-20', N'Canada'),
 (N'4',  N'Maria',   N'Garcia', N'maria.garcia@example.com',    N'555-0104', N'2019-07-08', N'Mexico'),
 (N'5',  N' Ahmed ', N'Hassan', N'ahmed.hassan@example.com',    N'555-0105', N'2023-02-14', N'UAE'),
 (N'6',  N'Sarah',   N'O''Neil',N'sarah.oneil@example.com',     N'555-0106', N'2021-09-30', N'Ireland'),
 (N'7',  N'Oskar',   N'Lee',    N'not-an-email',                N'555-0107', N'2022-05-01', N'Singapore'),
 (N'',   N'Tom',     N'Baker',  N'tom.baker@example.com',       N'555-0108', N'2020-12-25', N'UK'),
 (N'9',  N'Lena',    N'Novak',  N'lena.novak@example.com',      N'555-0109', N'2021-04-11', N'Czech Republic'),
 (N'10', N'Carlos',  N'Diaz',   N'carlos.diaz@example.com',     N'555-0110', N'2023-08-19', N'Spain');

-- Row-level cleansing performed by SCR_CleanseCustomerRow
INSERT INTO dbo.StagingCustomers
    (CustomerID, FirstName, LastName, FullName, Email, CleanEmail, Phone, SignupDate, Country, IsValidRow, LoadDateTime)
SELECT
    c.CustomerID,
    c.FirstName,
    c.LastName,
    LTRIM(RTRIM(LTRIM(RTRIM(c.FirstName)) + N' ' + LTRIM(RTRIM(c.LastName)))),
    c.Email,
    LOWER(LTRIM(RTRIM(c.Email))),
    c.Phone,
    c.SignupDate,
    c.Country,
    -- NULLIF(...,'') matters: T-SQL TRY_CAST('' AS INT) yields 0, but the C#
    -- Script Component uses int.TryParse("") which is false. Without this the
    -- blank-CustomerID row would be wrongly flagged valid.
    CASE WHEN TRY_CAST(NULLIF(LTRIM(RTRIM(c.CustomerID)), N'') AS INT) IS NOT NULL
          AND LOWER(LTRIM(RTRIM(c.Email))) LIKE N'%@%'
          AND LOWER(LTRIM(RTRIM(c.Email))) LIKE N'%.%'
          AND LEN(LTRIM(RTRIM(c.FirstName))) > 0
         THEN 1 ELSE 0 END,
    SYSDATETIME()
FROM @csv c;
GO

-- ---- DFT_BuildSalesSummary : join + aggregate, then the Script Component's tier ----
INSERT INTO dbo.SalesSummary
    (CustomerID, FullName, Email, TotalOrders, TotalRevenue, LastOrderDate, CustomerTier, ProcessedDateTime)
SELECT
    s.CustomerID,
    s.FullName,
    s.Email,
    s.TotalOrders,
    s.TotalRevenue,
    s.LastOrderDate,
    CASE WHEN s.TotalRevenue >= 10000 THEN N'Platinum'
         WHEN s.TotalRevenue >=  5000 THEN N'Gold'
         WHEN s.TotalRevenue >=  1000 THEN N'Silver'
         ELSE N'Bronze' END,
    SYSDATETIME()
FROM (
    SELECT
        o.CustomerID,
        c.FullName,
        c.CleanEmail AS Email,
        COUNT(DISTINCT o.OrderID)             AS TotalOrders,
        SUM(od.Quantity * od.UnitPrice)       AS TotalRevenue,
        MAX(o.OrderDate)                      AS LastOrderDate
    FROM dbo.Orders o
    INNER JOIN dbo.OrderDetails od ON od.OrderID = o.OrderID
    INNER JOIN dbo.StagingCustomers c ON o.CustomerID = TRY_CAST(c.CustomerID AS INT)
    WHERE ISNULL(c.IsValidRow, 1) = 1
    GROUP BY o.CustomerID, c.FullName, c.CleanEmail
) s;
GO

PRINT '===== dbo.StagingCustomers =====';
SELECT CustomerID, FullName, CleanEmail, IsValidRow FROM dbo.StagingCustomers ORDER BY TRY_CAST(CustomerID AS INT);

PRINT '===== dbo.SalesSummary =====';
SELECT CustomerID, FullName, TotalOrders, TotalRevenue, LastOrderDate, CustomerTier
FROM dbo.SalesSummary ORDER BY CustomerID;

PRINT '===== row counts =====';
SELECT
    (SELECT COUNT(*) FROM dbo.StagingCustomers)                     AS StagingRows,
    (SELECT COUNT(*) FROM dbo.StagingCustomers WHERE IsValidRow = 0) AS StagingInvalid,
    (SELECT COUNT(*) FROM dbo.SalesSummary)                          AS SummaryRows;
GO
