# HTA Data Import Tool

Standalone tool to import customer and ticket data from HTA Pro SQL table into NopCommerce database.

## Prerequisites

- .NET 7.0 SDK or later
- SQL Server database (NopCommerce)
- TempAllClientInfo table populated with HTA Pro data

## Features

### 1. Data Import
- Imports customers and tickets from `TempAllClientInfo` table
- Foreign key mapping (Country, StateProvince, CourtLocation, OffenceType, Source)
- Auto-generates FileNumbers in YYYY-MM-XXX format
- Assigns "Client" role to imported customers
- Tracks import metadata (ImportedFromHTA, ImportedFromFirm)
- Supports selective import using ticket filter file
- Dry run mode for testing

### 2. Post-Import Updates
- Creates ImportMapping table to link source records with imported data
- Maps TempAllClientInfo.POT to Ticket.Id and Customer.Id
- Updates Customer.CreatedOnUtc with IntakeDate from source
- Verifies all mappings and updates
- Identifies unmapped records

## Setup

1. Update `appsettings.json` with your configuration:
   - **ConnectionString**: Your SQL Server connection string
   - **StoreId**: The store ID to use (e.g., 1)
   - **FirmName**: The firm name for imports (e.g., "LegalAction")
   - **FilterCsvPath**: (Optional) Path to CSV file with ticket numbers to import
   - **DryRun**: Set to `true` to test, `false` to actually import/update

## Running

```bash
# Restore packages (first time only)
dotnet restore

# Build
dotnet build

# Run
dotnet run
```

When you run the tool, you'll see a menu:
```
Select operation:
1. Run Import (TempAllClientInfo → Customer & Ticket)
2. Run Post-Import Updates (Create mappings & update CreatedOnUtc)
```

### Option 1: Run Import
Use this to import new data from the TempAllClientInfo table into Customer and Ticket tables.

**What it does:**
- Reads data from TempAllClientInfo (applies filter if configured)
- Creates Customer records with proper foreign keys
- Creates Ticket records linked to customers
- Auto-generates FileNumbers based on IntakeDate
- Assigns Client role (ID 9) to all customers
- Sets ImportedFromHTA=1 and ImportedFromFirm

**Order of processing:**
- Records are sorted by IntakeDate ascending
- FileNumbers are generated sequentially per month (YYYY-MM-001, YYYY-MM-002, etc.)

### Option 2: Run Post-Import Updates
Use this AFTER you've imported data to create mappings and update dates.

**What it does:**
1. Creates `ImportMapping` table (if not exists)
2. Maps imported tickets back to source records using TicketNumber (POT)
3. Adds `ImportedTicketId` and `ImportedCustomerId` columns to `TempAllClientInfo`
4. Updates `TempAllClientInfo` with the imported IDs (reverse mapping)
5. Updates `Customer.CreatedOnUtc` to match source `IntakeDate`
6. Verifies all mappings and reports statistics

**When to use:**
- After initial import to update Customer.CreatedOnUtc
- To create permanent mapping between source and imported data
- To add imported IDs back to the source table for easy reference
- To identify any unmapped or mismatched records

## Configuration Details

### appsettings.json Example
```json
{
  "ConnectionString": "Data Source=SERVER;Initial Catalog=LegalShakDB;Integrated Security=False;User ID=sa;Password=****;Trust Server Certificate=True",
  "StoreId": 1,
  "FirmName": "LegalAction",
  "FilterCsvPath": "G:\\Projects\\HTADataImportTool\\HTADataImport\\ticket-filter-sample.csv",
  "DryRun": true
}
```

### Filter CSV Format
Create a simple CSV file with one column named "TicketNumber":
```csv
TicketNumber
AZ12345678
AZ87654321
AZ11223344
```

Only tickets matching these numbers will be imported.

## Workflow

### First-Time Import
1. Set `DryRun: true` in appsettings.json
2. Run option 1 (Import) to test
3. Review the dry run output
4. Set `DryRun: false`
5. Run option 1 (Import) to perform actual import
6. Set `DryRun: true`
7. Run option 2 (Post-Import Updates) to test mappings
8. Set `DryRun: false`
9. Run option 2 (Post-Import Updates) to apply updates

### Already Imported Data
If you've already imported data and need to update Customer.CreatedOnUtc:
1. Set `DryRun: false` in appsettings.json
2. Run option 2 (Post-Import Updates)
3. Review the output for any warnings or errors

## Database Tables

### Source Table
- **TempAllClientInfo**: Contains all HTA Pro data
  - After running post-import updates, these columns are added:
    - `ImportedTicketId` (INT NULL): The ID of the imported Ticket record
    - `ImportedCustomerId` (INT NULL): The ID of the imported Customer record

### Destination Tables
- **Customer**: Customer records with foreign keys
- **Ticket**: Ticket records linked to customers
- **Customer_CustomerRole_Mapping**: Assigns "Client" role
- **ImportMapping**: Maps source POT to imported IDs (created by post-import updates)
  - Columns: SourcePOT, TicketId, CustomerId, SourceIntakeDate, SourceFirstName, SourceLastName, SourceAddress, ImportedOnUtc

### Reference Tables (Used for Foreign Keys)
- **Country**: Country lookup (Canada)
- **StateProvince**: Province/State lookup (ON, AB, BC, etc.)
- **CourtLocation**: Court location lookup (by name and ICON code)
- **OffenceType**: Offence type lookup (by statute section and description)
- **Source**: Source of ticket (e.g., "HTA Pro")

## FileNumber Generation

FileNumbers are auto-generated in YYYY-MM-XXX format:
- YYYY: Year from IntakeDate
- MM: Month from IntakeDate
- XXX: Sequential 3-digit number starting from 001 for each month

Examples:
- 2024-03-001 (first ticket in March 2024)
- 2024-03-002 (second ticket in March 2024)
- 2024-04-001 (first ticket in April 2024)

## Foreign Key Mapping

### Country & StateProvince
- Matches province name or abbreviation (e.g., "Ontario" or "ON")
- Maps to Canadian provinces only
- Falls back to "Ontario" if not found

### CourtLocation
- Matches by court name OR ICON code
- Case-insensitive matching
- Logs warning if court not found

### OffenceType
- First attempts to match by statute section (e.g., "128(14)")
- Falls back to description matching if section not found
- Logs warning if offence type not found

### Source
- Looks up by source name (currently maps to "HTA Pro")
- Falls back to NULL if not found

## Troubleshooting

### "Unmapped records" warning
Some source records couldn't be matched to imported tickets. This can happen if:
- Ticket wasn't actually imported (due to errors or filters)
- TicketNumber (POT) doesn't match between source and ticket
- IsImported flag is not set on the ticket

**Solution**: Check the TempAllClientInfo table for records with POT values that don't exist in the Ticket table with IsImported=1.

### "Mismatched dates" warning
Customer.CreatedOnUtc doesn't match source IntakeDate after update. This shouldn't normally happen.

**Solution**: Run option 2 again to re-apply updates.

### Role assignment not working
Make sure the Client role exists with ID 9 in the CustomerRole table.

**Solution**: 
```sql
SELECT * FROM [dbo].[CustomerRole] WHERE Name = 'Client'
```

## Support

For issues or questions, contact the development team.