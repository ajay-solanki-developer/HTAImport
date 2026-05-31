-- =============================================
-- Migration Script: Add HTAClientId and HTATicketId columns
-- Date: 2026-05-30
-- Purpose: Store original Garner database IDs for future mapping
-- =============================================

USE [LegalShark30May26DB]-- Change to your database name
GO

-- =============================================
-- 1. Add HTAClientId column to Customer table
-- =============================================
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[Customer]') AND name = 'HTAClientId')
BEGIN
    PRINT 'Adding HTAClientId column to Customer table...'
    
    ALTER TABLE [dbo].[Customer]
    ADD [HTAClientId] NVARCHAR(MAX) NULL
    
    PRINT '✓ HTAClientId column added successfully'
END
ELSE
BEGIN
    PRINT '⚠ HTAClientId column already exists in Customer table'
END
GO

-- =============================================
-- 2. Add HTATicketId column to Ticket table
-- =============================================
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[Ticket]') AND name = 'HTATicketId')
BEGIN
    PRINT 'Adding HTATicketId column to Ticket table...'
    
    ALTER TABLE [dbo].[Ticket]
    ADD [HTATicketId] NVARCHAR(MAX) NULL
    
    PRINT '✓ HTATicketId column added successfully'
END
ELSE
BEGIN
    PRINT '⚠ HTATicketId column already exists in Ticket table'
END
GO

-- =============================================
-- 3. Add HTAClientId column to Ticket table (for reference)
-- =============================================
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[Ticket]') AND name = 'HTAClientId')
BEGIN
    PRINT 'Adding HTAClientId column to Ticket table...'
    
    ALTER TABLE [dbo].[Ticket]
    ADD [HTAClientId] NVARCHAR(MAX) NULL
    
    PRINT '✓ HTAClientId column added successfully'
END
ELSE
BEGIN
    PRINT '⚠ HTAClientId column already exists in Ticket table'
END
GO

-- =============================================
-- 4. Create indexes for performance (optional but recommended)
-- =============================================
PRINT 'Creating indexes for HTAClientId and HTATicketId...'

-- Index on Customer.HTAClientId
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Customer]') AND name = 'IX_Customer_HTAClientId')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Customer_HTAClientId]
    ON [dbo].[Customer] ([HTAClientId])
    WHERE [HTAClientId] IS NOT NULL
    
    PRINT '✓ Index IX_Customer_HTAClientId created'
END
ELSE
BEGIN
    PRINT '⚠ Index IX_Customer_HTAClientId already exists'
END
GO

-- Index on Ticket.HTATicketId
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Ticket]') AND name = 'IX_Ticket_HTATicketId')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Ticket_HTATicketId]
    ON [dbo].[Ticket] ([HTATicketId])
    WHERE [HTATicketId] IS NOT NULL
    
    PRINT '✓ Index IX_Ticket_HTATicketId created'
END
ELSE
BEGIN
    PRINT '⚠ Index IX_Ticket_HTATicketId already exists'
END
GO

-- Index on Ticket.HTAClientId
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Ticket]') AND name = 'IX_Ticket_HTAClientId')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Ticket_HTAClientId]
    ON [dbo].[Ticket] ([HTAClientId])
    WHERE [HTAClientId] IS NOT NULL
    
    PRINT '✓ Index IX_Ticket_HTAClientId created'
END
ELSE
BEGIN
    PRINT '⚠ Index IX_Ticket_HTAClientId already exists'
END
GO

PRINT ''
PRINT '======================================='
PRINT 'Migration completed successfully!'
PRINT '======================================='
PRINT ''
PRINT 'The following columns have been added:'
PRINT '  • Customer.HTAClientId - stores original pkClientID from GarnertblClient'
PRINT '  • Ticket.HTATicketId - stores original pkTicketID from GarnertblTicket'
PRINT '  • Ticket.HTAClientId - stores original fkClientID from GarnertblTicket'
PRINT ''
PRINT 'These IDs will be populated during the import process and can be used'
PRINT 'for future mapping and data synchronization with the original Garner database.'
PRINT ''
GO
