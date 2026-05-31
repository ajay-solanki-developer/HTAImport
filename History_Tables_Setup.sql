-- ============================================
-- History Tables Setup for HTA Data Import
-- Creates tables for tracking Disposition, Offence, and CourtDate history
-- ============================================

USE [LegalShark30May26DB];
GO

-- ============================================
-- 1. TicketDispositionHistory Table
-- Tracks all disposition changes for a ticket
-- ============================================

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[TicketDispositionHistory]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[TicketDispositionHistory](
        [Id] [int] IDENTITY(1,1) NOT NULL,
        [TicketId] [int] NOT NULL,
        [DispositionId] [int] NOT NULL,
        [DispositionName] [nvarchar](500) NULL,
        [ChangedBy] [nvarchar](255) NULL,
        [Notes] [nvarchar](max) NULL,
        [CreatedOnUtc] [datetime2](7) NOT NULL,
        CONSTRAINT [PK_TicketDispositionHistory] PRIMARY KEY CLUSTERED ([Id] ASC),
        CONSTRAINT [FK_TicketDispositionHistory_Ticket] FOREIGN KEY([TicketId])
            REFERENCES [dbo].[Ticket] ([Id])
            ON DELETE CASCADE,
        CONSTRAINT [FK_TicketDispositionHistory_Disposition] FOREIGN KEY([DispositionId])
            REFERENCES [dbo].[Disposition] ([pkDispositionID])
    );
    
    CREATE NONCLUSTERED INDEX [IX_TicketDispositionHistory_TicketId] 
        ON [dbo].[TicketDispositionHistory]([TicketId] ASC);
    
    CREATE NONCLUSTERED INDEX [IX_TicketDispositionHistory_CreatedOnUtc] 
        ON [dbo].[TicketDispositionHistory]([CreatedOnUtc] ASC);
    
    PRINT 'Table TicketDispositionHistory created successfully.';
END
ELSE
BEGIN
    PRINT 'Table TicketDispositionHistory already exists.';
END
GO

-- ============================================
-- 2. TicketOffenceHistory Table
-- Tracks all offence type changes for a ticket
-- ============================================

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[TicketOffenceHistory]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[TicketOffenceHistory](
        [Id] [int] IDENTITY(1,1) NOT NULL,
        [TicketId] [int] NOT NULL,
        [OffenceTypeId] [int] NOT NULL,
        [OffenceName] [nvarchar](500) NULL,
        [SectionNumber] [nvarchar](100) NULL,
        [SpeedingGoing] [int] NULL,
        [SpeedingInA] [int] NULL,
        [ChangedBy] [nvarchar](255) NULL,
        [Notes] [nvarchar](max) NULL,
        [CreatedOnUtc] [datetime2](7) NOT NULL,
        CONSTRAINT [PK_TicketOffenceHistory] PRIMARY KEY CLUSTERED ([Id] ASC),
        CONSTRAINT [FK_TicketOffenceHistory_Ticket] FOREIGN KEY([TicketId])
            REFERENCES [dbo].[Ticket] ([Id])
            ON DELETE CASCADE,
        CONSTRAINT [FK_TicketOffenceHistory_OffenceType] FOREIGN KEY([OffenceTypeId])
            REFERENCES [dbo].[OffenceType] ([Id])
    );
    
    CREATE NONCLUSTERED INDEX [IX_TicketOffenceHistory_TicketId] 
        ON [dbo].[TicketOffenceHistory]([TicketId] ASC);
    
    CREATE NONCLUSTERED INDEX [IX_TicketOffenceHistory_CreatedOnUtc] 
        ON [dbo].[TicketOffenceHistory]([CreatedOnUtc] ASC);
    
    PRINT 'Table TicketOffenceHistory created successfully.';
END
ELSE
BEGIN
    PRINT 'Table TicketOffenceHistory already exists.';
END
GO

-- ============================================
-- 3. Verify TicketCourtHistory Table
-- This table should already exist, but we verify it here
-- ============================================

IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[TicketCourtHistory]') AND type in (N'U'))
BEGIN
    PRINT 'Table TicketCourtHistory already exists.';
    
    -- Check if it has the expected columns
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[TicketCourtHistory]') AND name = 'CourtDate')
    BEGIN
        PRINT 'WARNING: TicketCourtHistory table exists but might be missing expected columns.';
    END
END
ELSE
BEGIN
    PRINT 'WARNING: Table TicketCourtHistory does not exist. Creating it...';
    
    CREATE TABLE [dbo].[TicketCourtHistory](
        [Id] [int] IDENTITY(1,1) NOT NULL,
        [TicketId] [int] NOT NULL,
        [StoreId] [int] NOT NULL,
        [IconId] [int] NULL,
        [CourtId] [int] NULL,
        [CourtDate] [datetime2](7) NULL,
        [CourtRoom] [nvarchar](100) NULL,
        [CourtTime] [time](7) NULL,
        [ClientWantsToAttend] [bit] NOT NULL DEFAULT(0),
        [InterpreterNeeded] [bit] NOT NULL DEFAULT(0),
        [InterpreterLanguage] [nvarchar](100) NULL,
        [Notes] [nvarchar](max) NULL,
        [CreatedOnUtc] [datetime2](7) NOT NULL,
        CONSTRAINT [PK_TicketCourtHistory] PRIMARY KEY CLUSTERED ([Id] ASC),
        CONSTRAINT [FK_TicketCourtHistory_Ticket] FOREIGN KEY([TicketId])
            REFERENCES [dbo].[Ticket] ([Id])
            ON DELETE CASCADE,
        CONSTRAINT [FK_TicketCourtHistory_CourtLocation] FOREIGN KEY([CourtId])
            REFERENCES [dbo].[CourtLocation] ([Id])
    );
    
    CREATE NONCLUSTERED INDEX [IX_TicketCourtHistory_TicketId] 
        ON [dbo].[TicketCourtHistory]([TicketId] ASC);
    
    CREATE NONCLUSTERED INDEX [IX_TicketCourtHistory_CourtDate] 
        ON [dbo].[TicketCourtHistory]([CourtDate] ASC);
    
    PRINT 'Table TicketCourtHistory created successfully.';
END
GO

-- ============================================
-- 4. Verification Queries
-- Check if tables exist and have data
-- ============================================

SELECT 
    'TicketCourtHistory' AS TableName,
    COUNT(*) AS RecordCount
FROM [dbo].[TicketCourtHistory]

UNION ALL

SELECT 
    'TicketDispositionHistory',
    COUNT(*)
FROM [dbo].[TicketDispositionHistory]

UNION ALL

SELECT 
    'TicketOffenceHistory',
    COUNT(*)
FROM [dbo].[TicketOffenceHistory];

GO

-- ============================================
-- 5. Sample Queries to View History
-- ============================================

-- View Disposition History for a specific ticket
/*
SELECT 
    tdh.Id,
    tdh.TicketId,
    t.TicketNumber,
    tdh.DispositionName,
    d.Description AS DispositionDescription,
    tdh.ChangedBy,
    tdh.CreatedOnUtc
FROM [dbo].[TicketDispositionHistory] tdh
INNER JOIN [dbo].[Ticket] t ON t.Id = tdh.TicketId
INNER JOIN [dbo].[Disposition] d ON d.pkDispositionID = tdh.DispositionId
WHERE tdh.TicketId = 12345  -- Replace with actual ticket ID
ORDER BY tdh.CreatedOnUtc DESC;
*/

-- View Offence History for a specific ticket
/*
SELECT 
    toh.Id,
    toh.TicketId,
    t.TicketNumber,
    toh.OffenceName,
    ot.Name AS OffenceTypeName,
    ot.Statute,
    toh.SectionNumber,
    toh.SpeedingGoing,
    toh.SpeedingInA,
    toh.ChangedBy,
    toh.CreatedOnUtc
FROM [dbo].[TicketOffenceHistory] toh
INNER JOIN [dbo].[Ticket] t ON t.Id = toh.TicketId
INNER JOIN [dbo].[OffenceType] ot ON ot.Id = toh.OffenceTypeId
WHERE toh.TicketId = 12345  -- Replace with actual ticket ID
ORDER BY toh.CreatedOnUtc DESC;
*/

-- View Court Date History for a specific ticket
/*
SELECT 
    tch.Id,
    tch.TicketId,
    t.TicketNumber,
    tch.CourtDate,
    tch.CourtRoom,
    tch.CourtTime,
    cl.Name AS CourtLocationName,
    tch.ClientWantsToAttend,
    tch.InterpreterNeeded,
    tch.InterpreterLanguage,
    tch.CreatedOnUtc
FROM [dbo].[TicketCourtHistory] tch
INNER JOIN [dbo].[Ticket] t ON t.Id = tch.TicketId
LEFT JOIN [dbo].[CourtLocation] cl ON cl.Id = tch.CourtId
WHERE tch.TicketId = 12345  -- Replace with actual ticket ID
ORDER BY tch.CourtDate DESC, tch.CreatedOnUtc DESC;
*/

-- ============================================
-- USAGE INSTRUCTIONS
-- ============================================
--
-- 1. Run this script to create the history tables
-- 2. Run the HTADataImporter to import tickets
-- 3. History entries will be automatically created for:
--    - Initial court date (TicketCourtHistory)
--    - Initial disposition (TicketDispositionHistory) - if exists
--    - Initial offence type (TicketOffenceHistory) - if exists
-- 4. Use the sample queries above to view history for specific tickets
--
-- ============================================
