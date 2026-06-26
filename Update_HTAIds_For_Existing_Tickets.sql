-- =============================================
-- Update Script: Backfill HTATicketId and HTAClientId for Existing Tickets
-- Date: 2026-06-02
-- Purpose: Update already-imported tickets with original Garner IDs
-- =============================================
-- This script updates existing imported tickets with their original 
-- Garner database IDs (pkTicketID and fkClientID) by matching on POT/TicketNumber
-- =============================================

USE [LegalShark30May26DB]  -- Change to your database name
GO

PRINT '======================================='
PRINT 'Starting HTATicketId Update Process'
PRINT '======================================='
PRINT ''

-- =============================================
-- Step 1: Verify columns exist
-- =============================================
PRINT 'Step 1: Verifying columns exist...'

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[Ticket]') AND name = 'HTATicketId')
BEGIN
    PRINT '❌ ERROR: HTATicketId column does not exist in Ticket table'
    PRINT '   Please run Migration_Add_HTAIds.sql first!'
    RETURN
END

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[Ticket]') AND name = 'HTAClientId')
BEGIN
    PRINT '❌ ERROR: HTAClientId column does not exist in Ticket table'
    PRINT '   Please run Migration_Add_HTAIds.sql first!'
    RETURN
END

PRINT '✓ Columns exist'
PRINT ''

-- =============================================
-- Step 2: Check current state (StoreId=9 only)
-- =============================================
PRINT 'Step 2: Checking current state (StoreId=9 only)...'

DECLARE @TotalImportedTickets INT
DECLARE @TicketsWithHTAId INT
DECLARE @TicketsNeedingUpdate INT

SELECT @TotalImportedTickets = COUNT(*)
FROM [dbo].[Ticket] T
INNER JOIN [dbo].[Customer] C ON T.CustomerId = C.Id
WHERE T.IsImported = 1
AND C.RegisteredInStoreId = 9

SELECT @TicketsWithHTAId = COUNT(*)
FROM [dbo].[Ticket] T
INNER JOIN [dbo].[Customer] C ON T.CustomerId = C.Id
WHERE T.IsImported = 1 
AND T.HTATicketId IS NOT NULL
AND C.RegisteredInStoreId = 9

SET @TicketsNeedingUpdate = @TotalImportedTickets - @TicketsWithHTAId

PRINT '  Total imported tickets: ' + CAST(@TotalImportedTickets AS NVARCHAR(10))
PRINT '  Already have HTATicketId: ' + CAST(@TicketsWithHTAId AS NVARCHAR(10))
PRINT '  Need update: ' + CAST(@TicketsNeedingUpdate AS NVARCHAR(10))
PRINT ''

IF @TicketsNeedingUpdate = 0
BEGIN
    PRINT '✓ All tickets already have HTATicketId populated!'
    PRINT '  No update needed.'
    RETURN
END

-- =============================================
-- Step 3: Skip slow checks, proceed to update
-- =============================================
PRINT 'Step 3: Starting update (using POT + date matching for duplicates)...'
PRINT ''

-- =============================================
-- Step 4: Perform the update (StoreId=9 only)
-- =============================================
PRINT 'Step 4: Updating tickets with HTATicketId and HTAClientId (StoreId=9)...'
PRINT '  Using multi-field matching: POT + OffenceDate for accuracy'
PRINT ''

BEGIN TRY
    BEGIN TRANSACTION

    -- Update using CTE to ensure one-to-one matching
    -- Match on: POT + OffenceDate (within 2 days tolerance for date conversion differences)
    ;WITH MatchedTickets AS (
        SELECT 
            T.Id AS TicketId,
            GT.pkTicketID,
            GT.fkClientID,
            ROW_NUMBER() OVER (
                PARTITION BY T.Id 
                ORDER BY 
                    -- Prioritize exact date match
                    CASE WHEN T.OffenceDate = DATEADD(DAY, TRY_CAST(GT.TicketDate AS INT), '1899-12-30') THEN 0 ELSE 1 END,
                    -- Then closest date
                    ABS(DATEDIFF(DAY, T.OffenceDate, DATEADD(DAY, TRY_CAST(GT.TicketDate AS INT), '1899-12-30'))),
                    -- Then by pkTicketID (prefer earlier records)
                    GT.pkTicketID
            ) AS RowNum
        FROM [dbo].[Ticket] T
        INNER JOIN [dbo].[Customer] C ON T.CustomerId = C.Id
        INNER JOIN [LegalSharkDB].[dbo].[GarnertblTicket] GT 
            ON T.TicketNumber = GT.POT
            AND (
                -- Match if dates are close (within 2 days) or if one is NULL
                T.OffenceDate IS NULL 
                OR GT.TicketDate IS NULL
                OR TRY_CAST(GT.TicketDate AS INT) IS NULL
                OR ABS(DATEDIFF(DAY, T.OffenceDate, 
                    CASE 
                        WHEN TRY_CAST(GT.TicketDate AS INT) IS NOT NULL AND TRY_CAST(GT.TicketDate AS INT) > 0 
                        THEN DATEADD(DAY, TRY_CAST(GT.TicketDate AS INT), '1899-12-30')
                        ELSE NULL 
                    END)) <= 2
            )
        WHERE T.IsImported = 1
            AND T.HTATicketId IS NULL
            AND C.RegisteredInStoreId = 9
    )
    UPDATE T
    SET 
        T.HTATicketId = CAST(M.pkTicketID AS NVARCHAR(MAX)),
        T.HTAClientId = CAST(M.fkClientID AS NVARCHAR(MAX))
    FROM [dbo].[Ticket] T
    INNER JOIN MatchedTickets M ON T.Id = M.TicketId
    WHERE M.RowNum = 1  -- Only take the best match

    DECLARE @UpdatedCount INT = @@ROWCOUNT

    COMMIT TRANSACTION

    PRINT '✓ Successfully updated ' + CAST(@UpdatedCount AS NVARCHAR(10)) + ' tickets'
    PRINT ''

END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION

    PRINT '❌ ERROR during update:'
    PRINT '   Error Number: ' + CAST(ERROR_NUMBER() AS NVARCHAR(10))
    PRINT '   Error Message: ' + ERROR_MESSAGE()
    PRINT '   Error Line: ' + CAST(ERROR_LINE() AS NVARCHAR(10))
    RETURN
END CATCH

-- =============================================
-- Step 5: Update Customer HTAClientId (StoreId=9 only)
-- =============================================
PRINT 'Step 5: Updating Customer HTAClientId (StoreId=9)...'
PRINT ''

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[Customer]') AND name = 'HTAClientId')
BEGIN
    PRINT '⚠ Warning: HTAClientId column does not exist in Customer table'
    PRINT '  Skipping customer update'
END
ELSE
BEGIN
    BEGIN TRY
        BEGIN TRANSACTION

        -- Update Customer HTAClientId by matching through Ticket (StoreId=9 only)
        UPDATE C
        SET C.HTAClientId = T.HTAClientId
        FROM [dbo].[Customer] C
        INNER JOIN [dbo].[Ticket] T ON C.Id = T.CustomerId
        WHERE T.IsImported = 1
            AND T.HTAClientId IS NOT NULL
            AND C.HTAClientId IS NULL
            AND C.RegisteredInStoreId = 9

        DECLARE @UpdatedCustomers INT = @@ROWCOUNT

        COMMIT TRANSACTION

        PRINT '✓ Successfully updated ' + CAST(@UpdatedCustomers AS NVARCHAR(10)) + ' customers'
        PRINT ''

    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION

        PRINT '❌ ERROR during customer update:'
        PRINT '   Error Message: ' + ERROR_MESSAGE()
    END CATCH
END

-- =============================================
-- Step 6: Quick verification
-- =============================================
PRINT 'Step 6: Verifying results...'
PRINT ''

SELECT @TicketsWithHTAId = COUNT(*)
FROM [dbo].[Ticket] T
INNER JOIN [dbo].[Customer] C ON T.CustomerId = C.Id
WHERE T.IsImported = 1 
AND T.HTATicketId IS NOT NULL
AND C.RegisteredInStoreId = 9

PRINT '  Tickets now with HTATicketId: ' + CAST(@TicketsWithHTAId AS NVARCHAR(10)) + ' of ' + CAST(@TotalImportedTickets AS NVARCHAR(10))
PRINT ''

-- Show sample of updated tickets
PRINT 'Sample of updated tickets (first 5):'
SELECT TOP 5
    T.Id,
    T.TicketNumber,
    T.HTATicketId,
    T.HTAClientId,
    C.FirstName,
    C.LastName
FROM [dbo].[Ticket] T
INNER JOIN [dbo].[Customer] C ON T.CustomerId = C.Id
WHERE T.IsImported = 1
    AND T.HTATicketId IS NOT NULL
    AND C.RegisteredInStoreId = 9
ORDER BY T.Id DESC

PRINT ''
PRINT '======================================='
PRINT '✓ Update completed successfully!'
PRINT '======================================='
PRINT ''
PRINT 'Next steps:'
PRINT '  1. Verify the HTATicketId values match your expectations'
PRINT '  2. Use these queries to check mappings:'
PRINT '     SELECT Id, TicketNumber, HTATicketId, HTAClientId FROM Ticket WHERE IsImported = 1'
PRINT '  3. Future imports will automatically populate these fields'
PRINT ''
GO
