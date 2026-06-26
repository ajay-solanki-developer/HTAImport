# Field Mapping Fixes - Customer Import

## Issues Fixed

### 1. CreatedDate (CreatedOnUtc) ✅ FIXED
**Problem:** CreatedOnUtc was being set to current timestamp (`DateTime.UtcNow`) instead of the IntakeDate from source data.

**Solution:** Changed to use IntakeDate with fallback:
```csharp
["CreatedOnUtc"] = ParseDate(record.IntakeDate) ?? DateTime.UtcNow,
```
- Now properly sets customer creation date to their IntakeDate
- Falls back to current time only if IntakeDate is null/invalid

### 2. Source Field ✅ FIXED
**Problem:** Source was not being read from Garner database or mapped to customers.

**Solution:** 
- Added JOIN to `GarnertblSource` table in data query
- Added `C.fkSourceID` and `SRC.[Source]` to SELECT statement
- Added `SourceId` and `SourceName` fields to HTARecord model
- Implemented `GetOrCreateSourceId()` method to lookup Source by name
- Maps source from Garner data, falls back to default "Data Import" source if not found

### 3. Phone Fields ✅ FIXED
**Problem:** All phone numbers were being consolidated into single "Phone" field. Individual phone fields were not being populated.

**Solution:** Added separate mappings for all available phone fields:
```csharp
["Phone"] = GetPrimaryPhone(record),          // Still keeps primary phone (Cell > Home > Business)
["HomePhone"] = record.HomePhone,             // Now mapped separately
["BusinessPhone"] = record.BusinessPhone,     // Now mapped separately  
["CellPhone"] = record.Cell,                  // Now mapped separately
```

**Source Data Available:**
- ✅ `homephone` → `HomePhone`
- ✅ `businessphone` → `BusinessPhone`
- ✅ `Cell` → `CellPhone`

**Not Available in Source Data:**
- ❌ `AlternePhone` - This field does not exist in GarnertblClient table
- ❌ `AlterneContact` - This field does not exist in GarnertblClient table

If these fields need to be populated, the source Garner database would need to be checked for alternate field names or these columns added to the source table.

### 4. BillingCompany ✅ FIXED
**Problem:** BillingCompanyId was being read from source but not mapped to Customer table.

**Solution:**
- Implemented `GetBillingCompanyId()` method to lookup BillingCompany by source ID
- Added mapping: `["BillingCompanyId"] = billingCompanyId`
- Looks up using `SourceBillingCompanyId` column in BillingCompany table
- Returns null if not found (graceful handling)

## Files Modified

### 1. HTADataImporter.cs
- Added LEFT JOIN to GarnertblSource table
- Added SourceId and SourceName to SELECT query
- Added Source field reading in ReadDataFromTable()
- Modified InsertCustomer() to include:
  - Source lookup logic
  - BillingCompany lookup logic
  - Separate phone field mappings
  - IntakeDate for CreatedOnUtc
- Added helper methods:
  - `GetOrCreateSourceId()`
  - `GetBillingCompanyId()`

### 2. Models/HTARecord.cs
- Added `SourceId` property
- Added `SourceName` property

## Database Schema Requirements

For these fixes to work, the Customer table should have the following columns:
- `CreatedOnUtc` (datetime)
- `SourceId` (int, FK to Source table)
- `HomePhone` (nvarchar)
- `BusinessPhone` (nvarchar)
- `CellPhone` (nvarchar)
- `BillingCompanyId` (int, FK to BillingCompany table)

The code uses dynamic schema detection (`GetCustomerTableSchema()`), so if a column doesn't exist, it will be skipped gracefully.

## Testing

After these changes:
1. Run master data setup to ensure Sources are imported
2. Run the import to verify:
   - Customer.CreatedOnUtc matches IntakeDate from source
   - Customer.SourceId is populated (not null/default)
   - Customer.HomePhone, BusinessPhone, CellPhone are populated separately
   - Customer.BillingCompanyId is populated where available

## Notes

- AlternePhone and AlterneContact fields are mentioned in the issue but do not exist in the GarnertblClient source table
- If these fields need to be populated, investigate the Garner database schema for alternate field names
- The dynamic schema detection ensures compatibility even if some columns don't exist in the Customer table
