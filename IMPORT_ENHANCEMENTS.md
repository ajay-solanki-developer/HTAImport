# HTA Data Import Enhancements - May 31, 2026

## Summary

This update adds support for:
1. **BillingCompany master data import**
2. **Guilty Section, PTS (demerit points), and guilty speeding information** to ticket import
3. **Duplicate prevention** for all imported data (already existed for Client role, now verified and documented)

---

## Changes Made

### 1. BillingCompany Master Data Import

#### New Files/Classes
- Added `BillingCompanyImportRow` class in `HTAMasterDataSetup.cs`
- Added `BillingCompaniesProcessed` property to `MasterDataSetupResult`

#### New Methods
- `SetupBillingCompanies()` - Imports billing companies from GarnertblBillingCompany
- `EnsureBillingCompany()` - Checks for duplicates before inserting

#### Database Mapping
Source: `GarnerTempDB.dbo.GarnertblBillingCompany`
- pkBillingCompanyID → (tracked for reference)
- CompanyName → CompanyName
- CompanyAddress → StreetAddress
- ContactName → ContactName
- Phone + Ext → PhoneNumber (combined)
- Email → Email
- Notes → Notes
- CVORNumber → (included in notes if needed)

Destination: `LegalShark30May26DB.dbo.BillingCompany`
- Filters by StoreId (default: 9 for Garner import)
- Checks for duplicates by CompanyName before inserting
- Sets IsActive = 1 by default

---

### 2. Guilty Section and PTS Data Import

#### Updated HTARecord Model
Added new properties to capture guilty offense and points data:
```csharp
public string? GuiltyOffenseSectionId { get; set; }
public string? GuiltyOffenseWordingId { get; set; }
public string? GuiltySpeedingGoing { get; set; }
public string? GuiltySpeedingInA { get; set; }
public string? OffensePoints { get; set; }
public string? BillingCompanyId { get; set; }
```

#### Updated Data Reading (HTADataImporter.cs)
Enhanced SQL query in `ReadDataFromTable()` to include:
- `T.fkGuiltyOffenseSectionID` → GuiltyOffenseSectionId
- `T.fkGuiltyOffenseWordingID` → GuiltyOffenseWordingId
- `T.GuiltySpeedingGoing` → GuiltySpeedingGoing
- `T.GuiltySpeedingInA` → GuiltySpeedingInA
- `OS.Points` → OffensePoints (from GarnertblOffenseSection join)
- `T.kBillingCompanyID` → BillingCompanyId

#### Updated Ticket Insert (HTADataImporter.cs)
Modified `InsertTicket()` method to populate:
- **GuiltySectionNumber** - from `fkGuiltyOffenseSectionID`
- **GuiltyWording** - from `fkGuiltyOffenseWordingID` (mapped to OffenceType)
- **GuiltySpeedInfo** - constructed from GuiltySpeedingGoing and GuiltySpeedingInA
- **PtsOffence** - parsed from OffensePoints (demerit points)
- **PtsDisp** - remains NULL until disposition is entered (handled by application)

---

### 3. Duplicate Prevention

All imports now check for duplicates before inserting:

#### Customer Role Assignment
- Already implemented: Uses LEFT JOIN to check if role mapping exists
- Only assigns Client role (RoleId: 6) to customers that don't already have it
- Filters by `ImportedFromHTA = 1` and recent `CreatedOnUtc` (within 10 minutes)

#### Master Data Imports
All master data `Ensure*` methods check for existing records:
- **BillingCompany**: Checks by StoreId + CompanyName
- **CourtLocation**: Checks by StoreId + Name
- **OffenceType**: Checks by StoreId + Statute (or Description)
- **Officer**: Checks by StoreId + BadgeNumber (or FirstName/LastName/DivisionNumber)
- **Disposition**: Checks by Description (global, no StoreId)
- **Source**: Checks by StoreId + Name
- **AreaOfPractice**: Checks by StoreId + Name
- **CourtJurisdiction**: Checks by StoreId + Name

---

## Database Schema Changes

### Ticket Table Columns Used
These columns already exist in the Ticket table and are now being populated:
- `GuiltySectionNumber` (nvarchar) - Section number of guilty plea
- `GuiltyWording` (int) - Foreign key to OffenceType for guilty plea
- `GuiltySpeedInfo` (nvarchar) - Speeding info for guilty plea
- `PtsOffence` (int) - Demerit points for the offence
- `PtsDisp` (int) - Demerit points after disposition (set later by application)
- `SectionNumber` (nvarchar) - Already being populated
- `DateSection7Filed` (datetime) - Not populated by import

### BillingCompany Table
Existing table structure (no changes needed):
- Id (int, PK)
- StoreId (int)
- CompanyName (nvarchar)
- ContactName (nvarchar)
- PhoneNumber (nvarchar)
- Email (nvarchar)
- StreetAddress (nvarchar)
- City (nvarchar)
- Province (nvarchar)
- PostalCode (nvarchar)
- Notes (nvarchar)
- IsActive (bit)
- CreatedOnUtc (datetime)
- UpdatedOnUtc (datetime)

---

## Usage Instructions

### Step 1: Run Master Data Setup
```
dotnet run
Select option: 1 (Setup Master Data)
```

This will import:
- Sources
- Court Jurisdictions
- Court Locations
- Area of Practice
- Offence Types (with Points from GarnertblOffenseSection)
- Officers
- Dispositions
- **Billing Companies** (NEW!)

All imports check for duplicates before inserting.

### Step 2: Run Data Import
```
dotnet run
Select option: 2 (Run Import)
```

This will import:
- Customers (with Client role assignment)
- Tickets (with guilty section, PTS, and guilty speeding info)

All imports skip duplicates:
- Customers: Grouped by FirstName + LastName + Address
- Client Role: Only assigned if not already present
- Tickets: Use unique HTATicketId/POT to avoid duplicates

### Step 3: Run Post-Import Updates
```
dotnet run
Select option: 3 (Run Post-Import Updates)
```

This creates mappings and updates CreatedOnUtc dates.

---

## Error Resolution

### Previous Error: "Invalid column name 'Section', 'PTS', 'GuiltySection'"

**Root Cause**: The code was not properly reading and mapping the guilty offense and points data from the source database.

**Resolution**: 
- Added proper JOIN to GarnertblOffenseSection to get Points data
- Added reading of fkGuiltyOffenseSectionID, fkGuiltyOffenseWordingID
- Added proper INSERT parameters for GuiltySectionNumber, GuiltyWording, GuiltySpeedInfo, PtsOffence
- These columns already exist in the Ticket table - we're now properly populating them

---

## Testing Checklist

Before running in production:
- [ ] Verify GarnertblBillingCompany data is available
- [ ] Verify GarnertblOffenseSection.Points values are correct
- [ ] Test with DryRun = true first
- [ ] Verify no duplicate records are created
- [ ] Check that PtsOffence is populated correctly
- [ ] Check that GuiltyWording links to correct OffenceType
- [ ] Verify BillingCompany records are created with correct StoreId

---

## Configuration

Update `appsettings.json`:
```json
{
  "ConnectionString": "Your connection string",
  "StoreId": 9,
  "FirmName": "Garner",
  "DryRun": false
}
```

**Important**: 
- StoreId = 9 is default for Garner import
- Set DryRun = true for testing
- Set DryRun = false for actual import

---

## Benefits

1. **Complete Data Migration**: Now imports all ticket offense data including guilty pleas
2. **Demerit Points Tracking**: PTS (points) are properly imported from source
3. **Billing Company Support**: Can track which billing company is associated with each ticket
4. **No Duplicates**: All imports check for existing records before inserting
5. **Client Role Assignment**: Automatically assigns Client role (RoleId: 6) to imported customers

---

## Notes

- Client role assignment already had duplicate prevention implemented
- All master data imports now explicitly check for duplicates
- BillingCompany import is scoped by StoreId like other master data
- Guilty section/wording fields link to the OffenceType table (foreign keys)
- PtsOffence is populated from source; PtsDisp is set later by the application after disposition
