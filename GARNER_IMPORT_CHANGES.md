# Garner Data Import Update - Change Summary

## Date: May 30, 2026
## Branch: GarnerDataImport_Ajay_Solanki

---

## Overview

The HTA Data Import Tool has been updated to import data directly from the **original Garner tables** instead of the denormalized `TempAllClientInfo` view. This change allows us to preserve the original database IDs for future mapping and data synchronization.

---

## What Changed

### 1. Data Source Change

**Before:**
- Data was read from `TempAllClientInfo` (denormalized view)

**After:**
- Data is now read from the original Garner tables:
  - **GarnertblClient** - Client/Customer information
  - **GarnertblTicket** - Ticket information
  - **GarnertblTicketType** - Ticket type lookup

### 2. New Fields Added

#### HTARecord Model (`Models/HTARecord.cs`)
Added two new properties to track original Garner database IDs:
- `HTATicketId` - Stores `pkTicketID` from GarnertblTicket
- `HTAClientId` - Stores `pkClientID` from GarnertblClient / `fkClientID` from GarnertblTicket
- `TicketType` - Stores ticket type description from GarnertblTicketType

#### Customer Table
- `HTAClientId` (NVARCHAR(MAX)) - Stores original client ID from Garner database

#### Ticket Table
- `HTATicketId` (NVARCHAR(MAX)) - Stores original ticket ID from Garner database
- `HTAClientId` (NVARCHAR(MAX)) - Stores original client ID reference from Garner database

### 3. Modified Components

#### `HTADataImporter.cs`
- Updated `ReadDataFromTable()` method:
  - Changed SQL query to join GarnertblClient, GarnertblTicket, and GarnertblTicketType
  - Added proper LEFT JOINs to link tables
  - Updated field mappings to match Garner table structure
  - Added HTATicketId and HTAClientId to the data extraction

- Updated `InsertCustomer()` method:
  - Added HTAClientId to the customer data map
  - Will store original Garner client ID when inserting customers

- Updated `InsertTicket()` method:
  - Added HTATicketId and HTAClientId to INSERT statement
  - Added parameters for these IDs
  - Stores original Garner IDs with each imported ticket

---

## Database Schema Changes Required

### IMPORTANT: Run Migration Script First!

Before running the import tool, you **MUST** run the database migration script to add the required columns:

**File:** `Migration_Add_HTAIds.sql`

**Steps:**
1. Open SQL Server Management Studio (SSMS)
2. Connect to your database server
3. Open `Migration_Add_HTAIds.sql`
4. Change the database name in the script (line 7) to match your database:
   ```sql
   USE [LegalShakDB]  -- Change this to your database name
   ```
5. Execute the script (F5)
6. Verify all columns were created successfully

**What the migration does:**
- Adds `HTAClientId` column to `Customer` table
- Adds `HTATicketId` column to `Ticket` table
- Adds `HTAClientId` column to `Ticket` table (for reference)
- Creates indexes on these columns for better performance

---

## Field Mappings

### Client/Customer Fields (from GarnertblClient)
| Garner Field | HTARecord Property | Customer Table Column |
|--------------|-------------------|----------------------|
| pkClientID | HTAClientId | HTAClientId |
| IntakeDate | IntakeDate | (used for sorting) |
| First_Name | FirstName | FirstName |
| Lastname | LastName | LastName |
| Address | Address | StreetAddress |
| City | City | City |
| Prov | Prov | County |
| Postal | Postal | ZipPostalCode |
| homephone | HomePhone | Phone |
| businessphone | BusinessPhone | (alternate phone) |
| Cell | Cell | (alternate phone) |
| Fax | Fax | Fax |
| fkGenderID | Gender | Gender |
| Notes | Notes | AdminComment |
| fkLanguageID | Language | (interpreted) |

### Ticket Fields (from GarnertblTicket)
| Garner Field | HTARecord Property | Ticket Table Column |
|--------------|-------------------|---------------------|
| pkTicketID | HTATicketId | HTATicketId |
| fkClientID | HTAClientId | HTAClientId |
| POT | POT | TicketNumber |
| fkIconID | ICON | IconId |
| TicketDate | TicketDate | OffenceDate |
| DateRetained | IntakeDate | DateRetained |
| Intake | Intake | DateEntered |
| 1stApp | FirstApp | CourtDate |
| Rm | Rm | CourtRoom |
| Time | Time | CourtTime |
| fkOffenseSectionID | SectionNumber | SectionNumber |
| fkOffenseWordingID | OffenseWording | Wording |
| SpeedingGoing | SpeedingGoing | (notes) |
| SpeedingInA | SpeedingInA | (notes) |
| fkOfficerID | BadgeNumber | OfficerId |
| fkCourtID | CourtName | CourtId |
| Disposition | Disposition | Disposition |
| Fee | Fee | Fee |
| BaseGST | Tax | Tax |
| BaseTotal | Total | Total |
| HePays | Fine | FineToPay |
| WePay | WePay | (custom field) |
| TotalPayments | Paid | TotalPaid |
| Balance | Balance | Balance |
| Notes | TicketNotes | Notes |
| SpecialInstructions | SpecialInstructions | SpecialInstructions |
| DateDisclosureRequested | DateDisclosureRequested | DateOfRequest |
| DateDisclosureReceived | DateDisclosureReceived | DateReceived |
| fkGuaranteeID | Guarantee | Guarantee |

---

## Benefits of This Change

### 1. **Preserved Data Integrity**
- Maintains original database IDs (pkTicketID, pkClientID)
- Allows bidirectional mapping between Garner and new database
- No data loss from denormalization

### 2. **Future Data Synchronization**
- Can easily update records based on original Garner IDs
- Supports incremental imports and updates
- Enables data verification and reconciliation

### 3. **Audit Trail**
- Clear relationship between imported data and source
- Can trace back to original records
- Supports debugging and data quality checks

### 4. **Lookup Integration**
- Properly handles foreign keys (fkClientID, fkTicketTypeID, etc.)
- Can join with other Garner lookup tables as needed
- Maintains referential integrity

---

## How to Use

### 1. Pre-requisites
- Run `Migration_Add_HTAIds.sql` to add required database columns
- Ensure GarnerTempDB database is accessible
- Verify connection string in `appsettings.json`

### 2. Configuration
Update `appsettings.json`:
```json
{
  "ConnectionString": "Data Source=YOUR_SERVER;Initial Catalog=YOUR_DATABASE;...",
  "StoreId": 1,
  "FirmName": "Garner Import",
  "FilterCsvPath": null,
  "DryRun": true
}
```

### 3. Run Import
```bash
# Test mode (DryRun = true)
dotnet run

# Production mode (DryRun = false)
# Update appsettings.json: "DryRun": false
dotnet run
```

### 4. Verify Results
Check the console output for:
- Number of records read from Garner tables
- Number of customers imported
- Number of tickets imported
- Any warnings or errors

---

## Testing Checklist

- [ ] Migration script executed successfully
- [ ] HTAClientId column exists in Customer table
- [ ] HTATicketId and HTAClientId columns exist in Ticket table
- [ ] Dry run completes without errors
- [ ] Sample data imported correctly
- [ ] HTAClientId populated in Customer records
- [ ] HTATicketId and HTAClientId populated in Ticket records
- [ ] Original POT numbers match between Garner and imported data
- [ ] Customer names and addresses match
- [ ] Financial data (Fee, Tax, Total) imported correctly

---

## Future Enhancements

### Potential Updates
1. **Incremental Import**
   - Use HTATicketId to identify already-imported tickets
   - Skip or update existing records based on modification dates

2. **Data Synchronization**
   - Create update queries using HTATicketId/HTAClientId
   - Sync changes from Garner back to LegalShak

3. **Lookup Table Integration**
   - Map fkOffenseSectionID to actual offense descriptions
   - Map fkCourtID to court names
   - Map fkOfficerID to officer details

4. **Validation Reports**
   - Compare imported data against Garner source
   - Identify discrepancies or missing data
   - Generate reconciliation reports

---

## Troubleshooting

### Issue: "Invalid column name 'HTAClientId'"
**Solution:** Run the migration script `Migration_Add_HTAIds.sql` first

### Issue: "Invalid object name 'GarnertblClient'"
**Solution:** Verify GarnerTempDB database connection and table names

### Issue: No records imported
**Solution:** 
- Check WHERE clause in filter CSV (if used)
- Verify data exists in GarnertblClient and GarnertblTicket
- Check console output for specific error messages

### Issue: NULL values in HTAClientId/HTATicketId
**Solution:**
- Verify pkTicketID and pkClientID are populated in Garner tables
- Check SQL query results manually in SSMS
- Ensure data types match (NVARCHAR(MAX))

---

## Contact

For questions or issues with this import tool, contact:
- **Developer:** Ajay Solanki
- **Branch:** GarnerDataImport_Ajay_Solanki
- **Date:** May 30, 2026

---

## Files Modified

1. `Models/HTARecord.cs` - Added HTATicketId, HTAClientId, TicketType properties
2. `HTADataImporter.cs` - Updated ReadDataFromTable, InsertCustomer, InsertTicket methods
3. `Migration_Add_HTAIds.sql` - New migration script (CREATE)
4. `GARNER_IMPORT_CHANGES.md` - This documentation (CREATE)
