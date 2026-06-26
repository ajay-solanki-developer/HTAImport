-- =============================================
-- Verification Queries for Garner Data Import
-- =============================================
-- Use these queries to verify data after import

USE [LegalShakDB]  -- Change to your database name
GO

-- =============================================
-- 1. Check if HTAClientId column exists in Customer table
-- =============================================
SELECT 
    COLUMN_NAME, 
    DATA_TYPE, 
    IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'Customer' 
AND COLUMN_NAME IN ('HTAClientId')
GO

-- =============================================
-- 2. Check if HTATicketId and HTAClientId columns exist in Ticket table
-- =============================================
SELECT 
    COLUMN_NAME, 
    DATA_TYPE, 
    IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'Ticket' 
AND COLUMN_NAME IN ('HTATicketId', 'HTAClientId')
GO

-- =============================================
-- 3. Count imported customers with HTAClientId
-- =============================================
SELECT 
    COUNT(*) AS TotalCustomers,
    COUNT(HTAClientId) AS CustomersWithHTAClientId,
    COUNT(*) - COUNT(HTAClientId) AS CustomersWithoutHTAClientId
FROM [dbo].[Customer]
WHERE ImportedFromHTA = 1
GO

-- =============================================
-- 4. Count imported tickets with HTATicketId
-- =============================================
SELECT 
    COUNT(*) AS TotalTickets,
    COUNT(HTATicketId) AS TicketsWithHTATicketId,
    COUNT(HTAClientId) AS TicketsWithHTAClientId
FROM [dbo].[Ticket]
WHERE IsImported = 1
GO

-- =============================================
-- 5. Sample imported customers with HTAClientId
-- =============================================
SELECT TOP 10
    Id AS NewCustomerId,
    HTAClientId AS OriginalGarnerClientId,
    FirstName,
    LastName,
    Email,
    City,
    ImportedFromFirm,
    CreatedOnUtc
FROM [dbo].[Customer]
WHERE ImportedFromHTA = 1
AND HTAClientId IS NOT NULL
ORDER BY CreatedOnUtc DESC
GO

-- =============================================
-- 6. Sample imported tickets with HTATicketId
-- =============================================
SELECT TOP 10
    Id AS NewTicketId,
    HTATicketId AS OriginalGarnerTicketId,
    HTAClientId AS OriginalGarnerClientId,
    CustomerId AS NewCustomerId,
    TicketNumber AS POT,
    FileNumber,
    OffenceDate,
    Fee,
    Total
FROM [dbo].[Ticket]
WHERE IsImported = 1
AND HTATicketId IS NOT NULL
ORDER BY Id DESC
GO

-- =============================================
-- 7. Verify Customer-Ticket relationship using HTAClientId
-- =============================================
SELECT TOP 10
    C.Id AS NewCustomerId,
    C.HTAClientId AS CustomerGarnerID,
    C.FirstName + ' ' + C.LastName AS CustomerName,
    T.Id AS NewTicketId,
    T.HTATicketId AS TicketGarnerID,
    T.HTAClientId AS TicketClientGarnerID,
    T.TicketNumber AS POT,
    CASE 
        WHEN C.HTAClientId = T.HTAClientId THEN '✓ Match'
        ELSE '✗ Mismatch'
    END AS IDMatch
FROM [dbo].[Customer] C
INNER JOIN [dbo].[Ticket] T ON C.Id = T.CustomerId
WHERE C.ImportedFromHTA = 1
AND T.IsImported = 1
ORDER BY C.Id DESC
GO

-- =============================================
-- 8. Find customers imported in last hour
-- =============================================
SELECT 
    COUNT(*) AS CustomersImportedLastHour
FROM [dbo].[Customer]
WHERE ImportedFromHTA = 1
AND CreatedOnUtc >= DATEADD(HOUR, -1, GETUTCDATE())
GO

-- =============================================
-- 9. Find tickets imported in last hour
-- =============================================
SELECT 
    COUNT(*) AS TicketsImportedLastHour
FROM [dbo].[Ticket]
WHERE IsImported = 1
AND Id IN (
    SELECT T.Id 
    FROM [dbo].[Ticket] T
    INNER JOIN [dbo].[Customer] C ON T.CustomerId = C.Id
    WHERE C.CreatedOnUtc >= DATEADD(HOUR, -1, GETUTCDATE())
)
GO

-- =============================================
-- 10. Comparison query with Garner source data
-- =============================================
-- This query verifies imported data against original Garner tables
-- Uncomment and modify the connection to GarnerTempDB if needed
/*
SELECT 
    -- Garner Original
    GT.pkTicketID AS GarnerTicketId,
    GT.POT AS GarnerPOT,
    GC.pkClientID AS GarnerClientId,
    GC.First_Name + ' ' + GC.Lastname AS GarnerClientName,
    
    -- Imported Data
    T.Id AS ImportedTicketId,
    T.HTATicketId AS StoredGarnerTicketId,
    T.TicketNumber AS ImportedPOT,
    C.Id AS ImportedCustomerId,
    C.HTAClientId AS StoredGarnerClientId,
    C.FirstName + ' ' + C.LastName AS ImportedClientName,
    
    -- Verification
    CASE WHEN GT.pkTicketID = T.HTATicketId THEN '✓' ELSE '✗' END AS TicketIdMatch,
    CASE WHEN GT.POT = T.TicketNumber THEN '✓' ELSE '✗' END AS POTMatch,
    CASE WHEN GC.pkClientID = C.HTAClientId THEN '✓' ELSE '✗' END AS ClientIdMatch

FROM [GarnerTempDB].[dbo].[GarnertblTicket] GT
LEFT JOIN [GarnerTempDB].[dbo].[GarnertblClient] GC ON GT.fkClientID = GC.pkClientID
LEFT JOIN [LegalShakDB].[dbo].[Ticket] T ON GT.pkTicketID = T.HTATicketId
LEFT JOIN [LegalShakDB].[dbo].[Customer] C ON GC.pkClientID = C.HTAClientId
WHERE T.IsImported = 1
*/
GO

-- =============================================
-- 11. Find any orphaned tickets (tickets without matching customer)
-- =============================================
SELECT 
    T.Id,
    T.HTATicketId,
    T.HTAClientId,
    T.CustomerId,
    T.TicketNumber
FROM [dbo].[Ticket] T
LEFT JOIN [dbo].[Customer] C ON T.CustomerId = C.Id
WHERE T.IsImported = 1
AND C.Id IS NULL
GO

-- =============================================
-- 12. Statistics Summary
-- =============================================
SELECT 
    'Customers' AS EntityType,
    COUNT(*) AS Total,
    COUNT(HTAClientId) AS WithHTAId,
    COUNT(*) - COUNT(HTAClientId) AS WithoutHTAId,
    CAST(COUNT(HTAClientId) * 100.0 / COUNT(*) AS DECIMAL(5,2)) AS PercentageWithHTAId
FROM [dbo].[Customer]
WHERE ImportedFromHTA = 1

UNION ALL

SELECT 
    'Tickets' AS EntityType,
    COUNT(*) AS Total,
    COUNT(HTATicketId) AS WithHTAId,
    COUNT(*) - COUNT(HTATicketId) AS WithoutHTAId,
    CAST(COUNT(HTATicketId) * 100.0 / COUNT(*) AS DECIMAL(5,2)) AS PercentageWithHTAId
FROM [dbo].[Ticket]
WHERE IsImported = 1
GO

PRINT ''
PRINT '======================================='
PRINT 'Verification Queries Completed'
PRINT '======================================='
PRINT ''
PRINT 'Review the results above to ensure:'
PRINT '  ✓ HTAClientId and HTATicketId columns exist'
PRINT '  ✓ All imported records have HTA IDs populated'
PRINT '  ✓ Customer-Ticket relationships are correct'
PRINT '  ✓ HTAClientId in Ticket matches Customer HTAClientId'
PRINT ''
GO
