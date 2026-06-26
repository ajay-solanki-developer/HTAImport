# Master Data Reset - Quick Reference

## Quick Steps to Reset Master Data for StoreId 9

### Option 1: Using SQL Script (Manual)

1. **Verify Current State**
   ```sql
   -- Run Section 1 of MasterData_Reset_Verification.sql
   -- Shows current row counts
   ```

2. **Delete Master Data**
   ```sql
   -- Run Section 2 of MasterData_Reset_Verification.sql
   -- Deletes all master data for StoreId 9 in a transaction
   ```

3. **Verify Deletion**
   ```sql
   -- Run Section 3 of MasterData_Reset_Verification.sql
   -- All counts should be 0
   ```

4. **Recreate Master Data**
   ```csharp
   // Run HTAMasterDataSetup
   var setup = new HTAMasterDataSetup(
       connectionString: connString,
       sourceDatabaseName: "GarnerTempDB",
       storeId: 9,
       dryRun: false,
       clearExistingStoreMasterData: false,  // Already cleared in step 2
       clearGlobalDispositionData: false
   );
   var result = setup.SetupMasterData();
   ```

### Option 2: Using HTAMasterDataSetup (Automated)

```csharp
// Single command to clear and recreate all master data
var setup = new HTAMasterDataSetup(
    connectionString: connString,
    sourceDatabaseName: "GarnerTempDB",
    storeId: 9,
    dryRun: false,
    clearExistingStoreMasterData: true,      // ✅ Auto-clear before setup
    clearGlobalDispositionData: false        // Only if needed
);

var result = setup.SetupMasterData();
```

## Master Data Tables Affected (StoreId 9)

| Table              | Has StoreId | Action                        |
|--------------------|-------------|-------------------------------|
| AreaOfPractice     | ✅ Yes      | Deleted & Recreated           |
| CourtJurisdiction  | ✅ Yes      | Deleted & Recreated           |
| CourtLocation      | ✅ Yes      | Deleted & Recreated           |
| CourthouseRoom     | ✅ Yes      | Deleted & Recreated           |
| OffenceType        | ✅ Yes      | Deleted & Recreated           |
| Officer            | ✅ Yes      | Deleted & Recreated           |
| Source             | ✅ Yes      | Deleted & Recreated           |
| Disposition        | ❌ No       | Optional (global table)       |

## When to Use Each Option

### Use SQL Script (Option 1) when:
- You want manual control over each step
- You need to review data before deletion
- You're troubleshooting issues
- You want to run steps at different times

### Use Automated Setup (Option 2) when:
- You want a single-command solution
- You're doing a full refresh
- You trust the automated process
- You want to reduce manual steps

## Safety Features

✅ **Transaction-based** - All deletes in a single transaction  
✅ **Error Handling** - Automatic rollback on failure  
✅ **Dry Run Mode** - Test without making changes  
✅ **Foreign Key Safe** - Deletes in correct order  
✅ **StoreId Isolated** - Only affects StoreId 9  

## Typical Reset Workflow

```
1. Backup database (recommended)
   └─> SQL Server backup or snapshot

2. Run verification query
   └─> See current master data counts

3. Choose reset method:
   
   A. SQL Manual Reset
      ├─> Run Section 2 (delete script)
      └─> Run HTAMasterDataSetup with clearExisting = false
   
   OR
   
   B. Automated Reset
      └─> Run HTAMasterDataSetup with clearExisting = true

4. Verify master data created
   └─> Run Section 1 query again

5. Run HTADataImporter
   └─> Import tickets with new master data references
```

## Important Notes

⚠️ **Before Reset:**
- Backup your database
- Verify you're targeting the correct StoreId (9)
- Check if any tickets are already linked to master data
- Consider impact on existing calendar events

⚠️ **Disposition Table:**
- Does NOT have StoreId
- Shared across all stores
- Only clear if absolutely necessary
- Use `clearGlobalDispositionData: true` to clear

⚠️ **Calendar Events:**
- Will have CourtLocationId set to NULL
- Need to be re-linked after import if necessary
- Only affects StoreId 9 calendar events

## Verification Queries

### Check Master Data Counts
```sql
DECLARE @StoreId INT = 9;

SELECT 'Source' AS TableName, COUNT(*) AS TotalRows
FROM dbo.Source WHERE StoreId = @StoreId
UNION ALL
SELECT 'AreaOfPractice', COUNT(*) FROM dbo.AreaOfPractice WHERE StoreId = @StoreId
UNION ALL
SELECT 'CourtJurisdiction', COUNT(*) FROM dbo.CourtJurisdiction WHERE StoreId = @StoreId
UNION ALL
SELECT 'CourtLocation', COUNT(*) FROM dbo.CourtLocation WHERE StoreId = @StoreId
UNION ALL
SELECT 'CourthouseRoom', COUNT(*) FROM dbo.CourthouseRoom WHERE StoreId = @StoreId
UNION ALL
SELECT 'OffenceType', COUNT(*) FROM dbo.OffenceType WHERE StoreId = @StoreId
UNION ALL
SELECT 'Officer', COUNT(*) FROM dbo.Officer WHERE StoreId = @StoreId;
```

### Check for Orphaned References
```sql
-- Check tickets without valid court locations
SELECT COUNT(*) AS TicketsWithInvalidCourts
FROM Ticket T
LEFT JOIN CourtLocation CL ON T.CourtLocationId = CL.Id
WHERE T.StoreId = 9 AND T.CourtLocationId IS NOT NULL AND CL.Id IS NULL;

-- Check tickets without valid offence types
SELECT COUNT(*) AS TicketsWithInvalidOffences
FROM TicketOffence TO
LEFT JOIN OffenceType OT ON TO.OffenceTypeId = OT.Id
WHERE OT.Id IS NULL;
```

## Files Reference

- **MasterData_Reset_Verification.sql** - Manual SQL reset and verification
- **MASTERDATA_STOREID_IMPLEMENTATION.md** - Detailed implementation documentation
- **HTAMasterDataSetup.cs** - C# class for automated setup
- **HTADataImporter.cs** - Main import class (uses master data)
