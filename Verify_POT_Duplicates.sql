-- =============================================
-- Diagnostic Script: Quick POT Duplicates Check (StoreId=9 Only)
-- Date: 2026-06-02  
-- Purpose: Fast check for duplicate POTs affecting StoreId=9 tickets
-- =============================================

USE [LegalShark30May26DB]  -- Change to your database name
GO

PRINT '======================================='
PRINT 'Quick POT Duplicate Check (StoreId=9)'
PRINT '======================================='
PRINT ''

-- =============================================
-- 1. Summary only - fast query
-- =============================================
PRINT '1. Duplicate POT summary:'
PRINT ''

SELECT 
    'Imported tickets (StoreId=9)' AS Category,
    COUNT(*) AS Count
FROM [dbo].[Ticket] T
INNER JOIN [dbo].[Customer] C ON T.CustomerId = C.Id
WHERE T.IsImported = 1 AND C.RegisteredInStoreId = 9

UNION ALL

SELECT 
    'With HTATicketId populated' AS Category,
    COUNT(*) AS Count
FROM [dbo].[Ticket] T
INNER JOIN [dbo].[Customer] C ON T.CustomerId = C.Id
WHERE T.IsImported = 1 AND T.HTATicketId IS NOT NULL AND C.RegisteredInStoreId = 9

UNION ALL

SELECT 
    'Need HTATicketId update' AS Category,
    COUNT(*) AS Count
FROM [dbo].[Ticket] T
INNER JOIN [dbo].[Customer] C ON T.CustomerId = C.Id
WHERE T.IsImported = 1 AND T.HTATicketId IS NULL AND C.RegisteredInStoreId = 9

PRINT ''
PRINT '======================================='
PRINT ''

-- =============================================
-- 2. Sample of tickets to be updated (first 10)
-- =============================================
PRINT '2. Sample tickets to be updated (first 10):'
PRINT ''

SELECT TOP 10
    T.Id,
    T.TicketNumber AS POT,
    T.OffenceDate,
    C.FirstName + ' ' + C.LastName AS CustomerName
FROM [dbo].[Ticket] T
INNER JOIN [dbo].[Customer] C ON T.CustomerId = C.Id
WHERE T.IsImported = 1 
    AND T.HTATicketId IS NULL
    AND C.RegisteredInStoreId = 9
ORDER BY T.Id

PRINT ''
PRINT '======================================='
PRINT 'Quick check complete!'  
PRINT 'Run Update_HTAIds_For_Existing_Tickets.sql to update'
PRINT '======================================='
GO
