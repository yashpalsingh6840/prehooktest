USE SSISDemo;
GO

-- Landing table for the CSV file loaded by DFT - Load Customers From CSV.
-- Columns stay string-typed to mirror the raw file; the row-level Script
-- Component adds FullName / CleanEmail / IsValidRow / LoadDateTime.
IF OBJECT_ID(N'dbo.StagingCustomers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StagingCustomers
    (
        CustomerID      NVARCHAR(20)    NULL,
        FirstName       NVARCHAR(100)   NULL,
        LastName        NVARCHAR(100)   NULL,
        FullName        NVARCHAR(200)   NULL,
        Email           NVARCHAR(200)   NULL,
        CleanEmail      NVARCHAR(200)   NULL,
        Phone           NVARCHAR(50)    NULL,
        SignupDate      NVARCHAR(50)    NULL,
        Country         NVARCHAR(100)   NULL,
        IsValidRow      BIT             NULL,
        LoadDateTime    DATETIME2       NULL
    );
END
GO

-- "Many SQL tables" source side #1: order headers.
IF OBJECT_ID(N'dbo.Orders', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Orders
    (
        OrderID     INT IDENTITY(1,1) PRIMARY KEY,
        CustomerID  INT             NOT NULL,
        OrderDate   DATE            NOT NULL,
        Status      NVARCHAR(20)    NOT NULL
    );
END
GO

-- "Many SQL tables" source side #2: order line items.
IF OBJECT_ID(N'dbo.OrderDetails', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OrderDetails
    (
        OrderDetailID   INT IDENTITY(1,1) PRIMARY KEY,
        OrderID         INT             NOT NULL REFERENCES dbo.Orders(OrderID),
        ProductName     NVARCHAR(100)   NOT NULL,
        Quantity        INT             NOT NULL,
        UnitPrice       FLOAT           NOT NULL
    );
END
GO

-- Output of DFT - Build Sales Summary (joins Orders + OrderDetails +
-- StagingCustomers, then a row-level Script Component computes CustomerTier).
IF OBJECT_ID(N'dbo.SalesSummary', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SalesSummary
    (
        CustomerID          INT             NOT NULL,
        FullName            NVARCHAR(200)   NULL,
        Email               NVARCHAR(200)   NULL,
        TotalOrders         INT             NULL,
        TotalRevenue        FLOAT           NULL,
        LastOrderDate       DATE            NULL,
        CustomerTier        NVARCHAR(20)    NULL,
        ProcessedDateTime   DATETIME2       NULL
    );
END
GO

-- Written to by both Script Tasks (C# and VB.NET) via a raw OLE DB connection.
IF OBJECT_ID(N'dbo.EtlAuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EtlAuditLog
    (
        AuditID         INT IDENTITY(1,1) PRIMARY KEY,
        PackageName     NVARCHAR(200)   NULL,
        StepName        NVARCHAR(200)   NULL,
        EventTime       DATETIME2       NOT NULL DEFAULT SYSDATETIME(),
        RowsAffected    INT             NULL,
        Message         NVARCHAR(1000)  NULL
    );
END
GO
