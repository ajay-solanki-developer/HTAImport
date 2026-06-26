# History Tracking - Quick Start Guide

## What's New?
The HTA Data Importer now automatically tracks history for:
- 📅 **Court Dates** - Every court appearance
- ⚖️ **Dispositions** - Every disposition change
- 📝 **Offences** - Every offence type change

## Quick Setup (3 Steps)

### 1️⃣ Create History Tables
```sql
-- Open SQL Server Management Studio
-- Run: History_Tables_Setup.sql
-- This creates 3 tables:
--   • TicketCourtHistory
--   • TicketDispositionHistory  
--   • TicketOffenceHistory
```

### 2️⃣ Run Import
```bash
# Run the application
HTADataImport.exe

# Choose Option 2: Run Import
# History is automatically created for each ticket!
```

### 3️⃣ View History
```sql
-- Replace 123 with your ticket ID
DECLARE @TicketId INT = 123;

-- View all court dates
SELECT CourtDate, CourtRoom, CourtTime, CreatedOnUtc
FROM TicketCourtHistory
WHERE TicketId = @TicketId
ORDER BY CreatedOnUtc;

-- View disposition changes
SELECT d.Description, tdh.CreatedOnUtc, tdh.ChangedBy
FROM TicketDispositionHistory tdh
JOIN Disposition d ON d.pkDispositionID = tdh.DispositionId
WHERE tdh.TicketId = @TicketId
ORDER BY tdh.CreatedOnUtc;

-- View offence changes
SELECT ot.Name, ot.Statute, toh.SpeedingGoing, toh.SpeedingInA, toh.CreatedOnUtc
FROM TicketOffenceHistory toh
JOIN OffenceType ot ON ot.Id = toh.OffenceTypeId
WHERE toh.TicketId = @TicketId
ORDER BY toh.CreatedOnUtc;
```

## What Gets Tracked?

### During Import
Every ticket gets history records created for:

✅ **Court History** (always created)
- Court date and time
- Court location
- Court room
- Interpreter needs

✅ **Disposition History** (if disposition exists)
- Disposition type (e.g., "Guilty Plea", "Withdrawn")
- When it was set
- Changed by "HTA Data Import"

✅ **Offence History** (if offence mapped)
- Offence type
- Section number
- Speeding details (if applicable)
- Changed by "HTA Data Import"

## Common Queries

### Find tickets with most court appearances
```sql
SELECT 
    t.TicketNumber,
    c.FirstName + ' ' + c.LastName AS Customer,
    COUNT(*) AS CourtAppearances
FROM TicketCourtHistory tch
JOIN Ticket t ON t.Id = tch.TicketId
JOIN Customer c ON c.Id = t.CustomerId
WHERE t.StoreId = 9
GROUP BY t.TicketNumber, c.FirstName, c.LastName
HAVING COUNT(*) > 1
ORDER BY CourtAppearances DESC;
```

### Find speeding tickets with speeds
```sql
SELECT 
    t.TicketNumber,
    toh.SpeedingGoing AS Speed,
    toh.SpeedingInA AS Limit,
    (toh.SpeedingGoing - toh.SpeedingInA) AS Over,
    ot.Name AS Offence
FROM TicketOffenceHistory toh
JOIN Ticket t ON t.Id = toh.TicketId
JOIN OffenceType ot ON ot.Id = toh.OffenceTypeId
WHERE toh.SpeedingGoing IS NOT NULL
  AND t.StoreId = 9
ORDER BY (toh.SpeedingGoing - toh.SpeedingInA) DESC;
```

### View complete ticket timeline
```sql
DECLARE @TicketNumber NVARCHAR(50) = 'AB123456';

-- Get ticket ID
DECLARE @TicketId INT = (SELECT Id FROM Ticket WHERE TicketNumber = @TicketNumber);

-- Show all events in chronological order
SELECT 'Court' AS EventType, CourtDate AS EventDate, CourtRoom AS Details, CreatedOnUtc
FROM TicketCourtHistory WHERE TicketId = @TicketId

UNION ALL

SELECT 'Disposition', CreatedOnUtc, DispositionName, CreatedOnUtc
FROM TicketDispositionHistory WHERE TicketId = @TicketId

UNION ALL

SELECT 'Offence', CreatedOnUtc, OffenceName, CreatedOnUtc
FROM TicketOffenceHistory WHERE TicketId = @TicketId

ORDER BY CreatedOnUtc;
```

## Important Notes

### 🔒 Data Integrity
- History tables use CASCADE DELETE
- When a ticket is deleted, all its history is also deleted
- No orphaned records

### 📊 Initial Import
- All imported tickets get history records
- ChangedBy = "HTA Data Import"
- Timestamps show when import ran

### 🔄 Future Updates
- When tickets are updated in the app, new history entries should be added
- ChangedBy should be set to the username
- Original import history is preserved

### ⚡ Performance
- All history tables are indexed
- Fast queries by TicketId
- Fast chronological queries by CreatedOnUtc

## Files Reference

| File | Purpose |
|------|---------|
| [History_Tables_Setup.sql](History_Tables_Setup.sql) | SQL script to create tables |
| [HISTORY_TRACKING_IMPLEMENTATION.md](HISTORY_TRACKING_IMPLEMENTATION.md) | Detailed documentation |
| [HTADataImporter.cs](HTADataImporter.cs) | Updated import code |

## Troubleshooting

❌ **"Invalid object name 'TicketDispositionHistory'"**
- Run History_Tables_Setup.sql first

❌ **"Foreign key conflict"**
- Ensure master data setup ran first (Option 1)
- Verify Disposition/OffenceType tables have data

❌ **No history records created**
- Check if DryRun mode is enabled (should be false)
- Verify tickets were actually imported
- Check for errors in import log

## Next Steps

1. ✅ Run History_Tables_Setup.sql
2. ✅ Run Option 1: Master Data Setup (if not done)
3. ✅ Run Option 2: Data Import
4. ✅ Run queries to verify history
5. 📊 Generate reports using history data

---

**Need Help?** See [HISTORY_TRACKING_IMPLEMENTATION.md](HISTORY_TRACKING_IMPLEMENTATION.md) for detailed documentation.
