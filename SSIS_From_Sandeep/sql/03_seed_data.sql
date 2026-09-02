USE SSISDemo;
GO

-- Seed Orders + OrderDetails once. CustomerID values line up with
-- sample-data\Customers.csv: customer 7 has a deliberately bad email and
-- customer 8's row has a blank CustomerID in the CSV, so both should be
-- excluded from dbo.SalesSummary by the "WHERE c.IsValidRow = 1" join in
-- DFT - Build Sales Summary. Customer 10 has no orders at all, which is
-- also filtered out naturally by the INNER JOIN.
IF NOT EXISTS (SELECT 1 FROM dbo.Orders)
BEGIN
    INSERT INTO dbo.Orders (CustomerID, OrderDate, Status) VALUES
        (1, '2024-01-05', 'Completed'),  -- OrderID 1
        (1, '2024-03-20', 'Completed'),  -- OrderID 2
        (2, '2024-01-15', 'Completed'),  -- OrderID 3
        (3, '2024-03-01', 'Completed'),  -- OrderID 4
        (4, '2024-01-22', 'Completed'),  -- OrderID 5
        (5, '2024-02-18', 'Completed'),  -- OrderID 6
        (6, '2024-03-09', 'Completed'),  -- OrderID 7
        (7, '2024-01-30', 'Completed'),  -- OrderID 8  (customer 7 is invalid -> excluded downstream)
        (9, '2024-02-25', 'Completed');  -- OrderID 9

    INSERT INTO dbo.OrderDetails (OrderID, ProductName, Quantity, UnitPrice) VALUES
        (1, 'Laptop Dock',        2, 1200.00),
        (1, 'Wireless Mouse',     1,  500.00),
        (2, 'Server Rack Unit',   3, 2500.00),
        (3, 'Laptop Dock',        1, 1200.00),
        (3, 'USB Hub',            2,  300.00),
        (4, 'Wireless Mouse',     5,  500.00),
        (4, 'Monitor Arm',        1, 3200.00),
        (5, 'Keyboard',           1,  150.00),
        (6, 'Webcam',            10,   90.00),
        (7, 'Headset',            2,  600.00),
        (8, 'Docking Station',    1,  450.00),
        (9, 'Monitor Stand',      4,  250.00);
END
GO
