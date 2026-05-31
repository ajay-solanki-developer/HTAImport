# Master Data StoreId Filtering - Implementation Summary

## Overview
This document describes the changes made to ensure that the HTA Data Import system properly filters and references master data by StoreId (specifically StoreId = 9 for Garner import).

## Changes Made

### 1. HTADataImporter.cs - Officer Cache Initialization

**Location:** `InitializeLookupCaches()` method (around line 362)

**Change:**
- **BEFORE:** Officers were loaded without StoreId filtering
  ```csharp
  var officerCmd = new SqlCommand("SELECT Id, BadgeNumber FROM Officer WHERE BadgeNumber IS NOT NULL", connection);
  ```

- **AFTER:** Officers are now filtered by StoreId
  ```csharp
  var officerCmd = new SqlCommand($"SELECT Id, BadgeNumber FROM Officer WHERE StoreId = {_storeId} AND BadgeNumber IS NOT NULL AND IsActive = 1", connection);
  ```

**Impact:** The importer now only loads officers for the current StoreId, preventing cross-store data contamination.

### 2. Verification of Existing StoreId Filtering

Confirmed that the following master data tables are already correctly filtered by StoreId:

- ✅ **CourtLocation** - Filtered by `StoreId = {_storeId}`
- ✅ **OffenceType** - Filtered by `StoreId = {_storeId}`
- ✅ **Source** - Filtered by `StoreId = {_storeId}`
- ✅ **Officer** - NOW filtered by `StoreId = {_storeId}` (fixed)
- ✅ **AreaOfPractice** - Filtered in HTAMasterDataSetup.cs
- ✅ **CourtJurisdiction** - Filtered in HTAMasterDataSetup.cs

**Note:** Disposition table does NOT have a StoreId column and is global across all stores.

## Master Data Setup Process

### HTAMasterDataSetup.cs

The master data setup class provides automated cleanup and re-import of master data from GarnerTempDB.

#### Key Features:

1. **Automatic Cleanup**
   - Set `clearExistingStoreMasterData: true` to automatically delete existing master data before setup
   - Deletes in proper order to respect foreign key relationships:
     - CourthouseRoom (child records)
     - CourtLocation
     - CourtJurisdiction
     - OffenceType
     - Officer
     - Source
     - AreaOfPractice

2. **StoreId Isolation**
   - All master data operations are scoped to the specified StoreId
   - Prevents accidental deletion or modification of other stores' data

3. **Dry Run Mode**
   - Test the setup process without making changes
   - Displays what would be created/deleted

### Usage Example

```csharp
var setup = new HTAMasterDataSetup(
    connectionString: "your_connection_string",
    sourceDatabaseName: "GarnerTempDB",
    storeId: 9,                                  // Target store for Garner import
    dryRun: false,                               // Set to true to test without changes
    clearExistingStoreMasterData: true,          // Auto-clear before setup
    clearGlobalDispositionData: false            // Only set true if resetting Dispositions
);

var result = setup.SetupMasterData();

Console.WriteLine($"Sources processed: {result.SourcesProcessed}");
Console.WriteLine($"Courts processed: {result.CourtsProcessed}");
Console.WriteLine($"Offences processed: {result.OffencesProcessed}");
Console.WriteLine($"Officers processed: {result.OfficersProcessed}");
Console.WriteLine($"Dispositions processed: {result.DispositionsProcessed}");
```

## SQL Verification Scripts

The file `MasterData_Reset_Verification.sql` contains:

### Section 1: Verification Query
- Checks current master data row counts for StoreId 9
- Run this before and after reset to verify changes

### Section 2: Reset Query (Transaction-based)
- Safely deletes all master data for StoreId 9
- Uses transaction with error handling
- Rolls back on any failure

### Section 3: Post-Reset Verification
- Confirms all master data was deleted (should show 0 rows)

## Data Flow

```
┌─────────────────────────────────────────────────────────────┐
│ Master Data Setup Workflow                                  │
└─────────────────────────────────────────────────────────────┘

1. [OPTIONAL] Manual SQL Reset
   └─> Run MasterData_Reset_Verification.sql Section 2
   
2. HTAMasterDataSetup.SetupMasterData()
   │
   ├─> ClearExistingMasterDataForStore() [if enabled]
   │   └─> Deletes all master data for StoreId 9
   │
   ├─> SetupSources() 
   │   └─> Reads from GarnerTempDB.dbo.GarnertblSource
   │   └─> Creates in LegalShark DB with StoreId = 9
   │
   ├─> SetupCourtLocations()
   │   └─> Reads from GarnerTempDB.dbo.GarnertblCourt
   │   └─> Creates in LegalShark DB with StoreId = 9
   │
   ├─> SetupOffenceTypes()
   │   └─> Reads from GarnerTempDB.dbo.GarnertblOffenseSection
   │   └─> Creates in LegalShark DB with StoreId = 9
   │
   ├─> SetupOfficers()
   │   └─> Reads from GarnerTempDB.dbo.GarnertblOfficer
   │   └─> Creates in LegalShark DB with StoreId = 9
   │
   └─> SetupDispositions()
       └─> Reads from GarnerTempDB.dbo.GarnertblDisposition
       └─> Creates globally (no StoreId)

3. HTADataImporter.Import()
   │
   ├─> InitializeLookupCaches()
   │   └─> Loads master data FILTERED by StoreId = 9
   │
   └─> ImportTickets()
       └─> References only StoreId 9 master data
```

## Important Notes

1. **StoreId Consistency:** All master data operations use the same StoreId (9 for Garner)

2. **Disposition is Global:** The Disposition table does not have StoreId and is shared across all stores

3. **Foreign Key Safety:** The cleanup process deletes records in the correct order to avoid foreign key violations

4. **Transaction Safety:** All cleanup operations use transactions with rollback on error

5. **Dry Run Testing:** Always test with `dryRun: true` first to verify the setup process

## Testing Checklist

- [ ] Run verification query (Section 1) to see current state
- [ ] Run HTAMasterDataSetup with `dryRun: true`
- [ ] Review console output for expected operations
- [ ] Run HTAMasterDataSetup with `dryRun: false` and `clearExistingStoreMasterData: true`
- [ ] Run verification query (Section 3) to confirm setup
- [ ] Run HTADataImporter with a small test dataset
- [ ] Verify imported records reference correct StoreId 9 master data

## Troubleshooting

### Officers not being created during import
- Ensure HTAMasterDataSetup has been run first
- Check that officers exist in the Officer table with StoreId = 9
- Verify the BadgeNumber matches between source data and Officer table

### Courts/Offences not being matched
- Run HTAMasterDataSetup to ensure all master data is populated
- Check the console output for "unmapped" warnings during import
- Verify IconCode and court names match between source and destination

### Master data from wrong StoreId
- Verify the StoreId parameter is set correctly (should be 9)
- Check that InitializeLookupCaches is filtering by StoreId
- Run the verification query to confirm data is in the correct store

## Related Files

- `HTAMasterDataSetup.cs` - Master data setup and cleanup
- `HTADataImporter.cs` - Main import logic with StoreId filtering
- `MasterData_Reset_Verification.sql` - SQL verification and reset scripts
- `README.md` - General project documentation
