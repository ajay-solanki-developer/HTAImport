-- ============================================
-- HTA/Garner Master Data Reset and Verification
-- StoreId: 9
-- ============================================

USE [LegalShark30May26DB];
GO

DECLARE @StoreId INT = 9;

-- ============================================
-- SECTION 1: VERIFICATION - Check current master data counts
-- ============================================

SELECT 'Source' AS TableName, COUNT(*) AS TotalRows
FROM dbo.Source
WHERE StoreId = @StoreId

UNION ALL

SELECT 'AreaOfPractice', COUNT(*)
FROM dbo.AreaOfPractice
WHERE StoreId = @StoreId

UNION ALL

SELECT 'CourtJurisdiction', COUNT(*)
FROM dbo.CourtJurisdiction
WHERE StoreId = @StoreId

UNION ALL

SELECT 'CourtLocation', COUNT(*)
FROM dbo.CourtLocation
WHERE StoreId = @StoreId

UNION ALL

SELECT 'CourthouseRoom', COUNT(*)
FROM dbo.CourthouseRoom
WHERE StoreId = @StoreId

UNION ALL

SELECT 'OffenceType', COUNT(*)
FROM dbo.OffenceType
WHERE StoreId = @StoreId

UNION ALL

SELECT 'Officer', COUNT(*)
FROM dbo.Officer
WHERE StoreId = @StoreId;

GO

-- ============================================
-- SECTION 2: RESET - Delete all master data for StoreId 9
-- WARNING: This will permanently delete master data!
-- ============================================

USE [LegalShark30May26DB];
GO

DECLARE @StoreId INT = 9;

BEGIN TRANSACTION;

BEGIN TRY

    -- Delete children first (CourthouseRoom)
    DELETE CR
    FROM dbo.CourthouseRoom CR
    INNER JOIN dbo.CourtLocation CL
        ON CL.Id = CR.CourtId
    WHERE CR.StoreId = @StoreId
      AND CL.StoreId = @StoreId;

    -- Delete CourtLocation
    DELETE FROM dbo.CourtLocation
    WHERE StoreId = @StoreId;

    -- Delete CourtJurisdiction
    DELETE FROM dbo.CourtJurisdiction
    WHERE StoreId = @StoreId;

    -- Delete OffenceType
    DELETE FROM dbo.OffenceType
    WHERE StoreId = @StoreId;

    -- Delete Officer
    DELETE FROM dbo.Officer
    WHERE StoreId = @StoreId;

    -- Delete Source
    DELETE FROM dbo.Source
    WHERE StoreId = @StoreId;

    -- Delete AreaOfPractice
    DELETE FROM dbo.AreaOfPractice
    WHERE StoreId = @StoreId;

    -- Note: Disposition table does not have StoreId column
    -- If you need to clear Dispositions, use HTAMasterDataSetup with clearGlobalDispositionData = true

    COMMIT TRANSACTION;

    SELECT 'Successfully deleted all master data for StoreId ' + CAST(@StoreId AS VARCHAR) AS Result;

END TRY
BEGIN CATCH

    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;

    SELECT
        'Delete failed. Transaction rolled back.' AS Result,
        ERROR_NUMBER() AS ErrorNumber,
        ERROR_MESSAGE() AS ErrorMessage,
        ERROR_LINE() AS ErrorLine;

END CATCH;

GO

-- ============================================
-- SECTION 3: POST-RESET VERIFICATION
-- Run this after reset to confirm deletion
-- ============================================

USE [LegalShark30May26DB];
GO

DECLARE @StoreId INT = 9;

SELECT 
    'Source' AS TableName, 
    COUNT(*) AS RemainingRows
FROM dbo.Source
WHERE StoreId = @StoreId

UNION ALL

SELECT 'AreaOfPractice', COUNT(*)
FROM dbo.AreaOfPractice
WHERE StoreId = @StoreId

UNION ALL

SELECT 'CourtJurisdiction', COUNT(*)
FROM dbo.CourtJurisdiction
WHERE StoreId = @StoreId

UNION ALL

SELECT 'CourtLocation', COUNT(*)
FROM dbo.CourtLocation
WHERE StoreId = @StoreId

UNION ALL

SELECT 'CourthouseRoom', COUNT(*)
FROM dbo.CourthouseRoom
WHERE StoreId = @StoreId

UNION ALL

SELECT 'OffenceType', COUNT(*)
FROM dbo.OffenceType
WHERE StoreId = @StoreId

UNION ALL

SELECT 'Officer', COUNT(*)
FROM dbo.Officer
WHERE StoreId = @StoreId;

GO

-- ============================================
-- USAGE INSTRUCTIONS:
-- ============================================
-- 
-- 1. Run SECTION 1 to verify current master data counts
-- 2. Run SECTION 2 to delete all master data for StoreId 9
-- 3. Run SECTION 3 to verify deletion (all counts should be 0)
-- 4. Run HTAMasterDataSetup to recreate master data from GarnerTempDB
--
-- C# Usage:
-- 
--    var setup = new HTAMasterDataSetup(
--        connectionString: "your_connection_string",
--        sourceDatabaseName: "GarnerTempDB",
--        storeId: 9,
--        dryRun: false,
--        clearExistingStoreMasterData: true,   // Set to true to auto-clear before setup
--        clearGlobalDispositionData: false     // Set to true only if you need to reset Dispositions
--    );
--    
--    var result = setup.SetupMasterData();
-- 
-- ============================================
