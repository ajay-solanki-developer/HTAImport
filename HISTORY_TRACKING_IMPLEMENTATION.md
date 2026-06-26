# History Tracking Implementation - Disposition, Offence, and CourtDate

## Overview
The HTA Data Import system now tracks historical changes for three key ticket attributes:
1. **Disposition History** - Tracks all disposition changes
2. **Offence History** - Tracks all offence type changes
3. **CourtDate History** - Tracks all court date changes (already existed)

## Implementation Details

### 1. Database Tables

#### TicketDispositionHistory
Tracks all disposition changes for each ticket.

**Schema:**
```sql
CREATE TABLE [TicketDispositionHistory] (
    [Id] int IDENTITY(1,1) PRIMARY KEY,
    [TicketId] int NOT NULL,
    [DispositionId] int NOT NULL,
    [DispositionName] nvarchar(500),
    [ChangedBy] nvarchar(255),
    [Notes] nvarchar(max),
    [CreatedOnUtc] datetime2(7) NOT NULL,
    FOREIGN KEY ([TicketId]) REFERENCES [Ticket]([Id]) ON DELETE CASCADE,
    FOREIGN KEY ([DispositionId]) REFERENCES [Disposition]([pkDispositionID])
);
```

**Purpose:** Records when a ticket's disposition changes (e.g., from "Pending" to "Guilty Plea")

#### TicketOffenceHistory
Tracks all offence type changes for each ticket.

**Schema:**
```sql
CREATE TABLE [TicketOffenceHistory] (
    [Id] int IDENTITY(1,1) PRIMARY KEY,
    [TicketId] int NOT NULL,
    [OffenceTypeId] int NOT NULL,
    [OffenceName] nvarchar(500),
    [SectionNumber] nvarchar(100),
    [SpeedingGoing] int,
    [SpeedingInA] int,
    [ChangedBy] nvarchar(255),
    [Notes] nvarchar(max),
    [CreatedOnUtc] datetime2(7) NOT NULL,
    FOREIGN KEY ([TicketId]) REFERENCES [Ticket]([Id]) ON DELETE CASCADE,
    FOREIGN KEY ([OffenceTypeId]) REFERENCES [OffenceType]([Id])
);
```

**Purpose:** Records when a ticket's offence type changes (e.g., charge reduction from "Speeding 120/80" to "Disobey Sign")

#### TicketCourtHistory
Tracks all court date changes for each ticket (already existed).

**Schema:**
```sql
CREATE TABLE [TicketCourtHistory] (
    [Id] int IDENTITY(1,1) PRIMARY KEY,
    [TicketId] int NOT NULL,
    [StoreId] int NOT NULL,
    [IconId] int,
    [CourtId] int,
    [CourtDate] datetime2(7),
    [CourtRoom] nvarchar(100),
    [CourtTime] time(7),
    [ClientWantsToAttend] bit DEFAULT(0),
    [InterpreterNeeded] bit DEFAULT(0),
    [InterpreterLanguage] nvarchar(100),
    [Notes] nvarchar(max),
    [CreatedOnUtc] datetime2(7) NOT NULL,
    FOREIGN KEY ([TicketId]) REFERENCES [Ticket]([Id]) ON DELETE CASCADE,
    FOREIGN KEY ([CourtId]) REFERENCES [CourtLocation]([Id])
);
```

**Purpose:** Records all court appearances for a ticket (adjournments, trial dates, etc.)

### 2. Code Implementation

#### HTADataImporter.cs Changes

**New Methods Added:**

1. **InsertTicketDispositionHistory**
   ```csharp
   private void InsertTicketDispositionHistory(
       SqlConnection connection, 
       int ticketId, 
       int dispositionId, 
       string? dispositionName)
   ```
   - Creates a disposition history record
   - Called during import if a disposition exists
   - Records "HTA Data Import" as the ChangedBy value

2. **InsertTicketOffenceHistory**
   ```csharp
   private void InsertTicketOffenceHistory(
       SqlConnection connection, 
       int ticketId, 
       int offenceTypeId, 
       HTARecord record)
   ```
   - Creates an offence history record
   - Includes speeding details (SpeedingGoing, SpeedingInA)
   - Called during import if an offence type exists
   - Records "HTA Data Import" as the ChangedBy value

3. **ImportTickets Method Updated**
   ```csharp
   // After creating ticket, create history entries
   if (ticketId > 0)
   {
       // Court history
       InsertTicketCourtHistory(connection, ticketId, record);
       
       // Disposition history
       var dispositionId = GetDispositionId(record.Disposition);
       if (dispositionId.HasValue)
       {
           InsertTicketDispositionHistory(connection, ticketId, 
               dispositionId.Value, record.Disposition);
       }
       
       // Offence history
       var offenceTypeId = GetOffenceTypeId(record.SectionNumber, 
           record.OffenseWording);
       if (offenceTypeId.HasValue)
       {
           InsertTicketOffenceHistory(connection, ticketId, 
               offenceTypeId.Value, record);
       }
   }
   ```

### 3. Data Flow

```
┌─────────────────────────────────────────────────────────────┐
│ Ticket Import with History Tracking                         │
└─────────────────────────────────────────────────────────────┘

1. InsertTicket()
   └─> Creates main ticket record
   
2. InsertTicketCourtHistory()
   └─> Creates initial court date history
   └─> Always called if ticket created successfully
   
3. InsertTicketDispositionHistory()
   └─> Creates initial disposition history
   └─> Only called if disposition exists in source data
   
4. InsertTicketOffenceHistory()
   └─> Creates initial offence type history
   └─> Only called if offence type exists in source data
```

## Setup Instructions

### Step 1: Create History Tables
```sql
-- Run the History_Tables_Setup.sql script
-- This will create the three history tables if they don't exist
```

### Step 2: Run Import
```bash
# Run the HTA Data Importer
HTADataImport.exe

# Choose Option 2: Run Import
# History entries will be automatically created during import
```

### Step 3: Verify History Data
```sql
-- Check history record counts
SELECT 
    'TicketCourtHistory' AS TableName,
    COUNT(*) AS RecordCount
FROM TicketCourtHistory

UNION ALL

SELECT 'TicketDispositionHistory', COUNT(*)
FROM TicketDispositionHistory

UNION ALL

SELECT 'TicketOffenceHistory', COUNT(*)
FROM TicketOffenceHistory;
```

## Query Examples

### View All History for a Ticket
```sql
DECLARE @TicketId INT = 12345;  -- Replace with actual ticket ID

-- Court History
SELECT 'Court History' AS HistoryType,
    tch.CourtDate AS EventDate,
    cl.Name AS CourtName,
    tch.CourtRoom,
    tch.CourtTime,
    tch.CreatedOnUtc
FROM TicketCourtHistory tch
LEFT JOIN CourtLocation cl ON cl.Id = tch.CourtId
WHERE tch.TicketId = @TicketId
ORDER BY tch.CreatedOnUtc

-- Disposition History
SELECT 'Disposition History' AS HistoryType,
    tdh.CreatedOnUtc AS EventDate,
    d.Description AS DispositionName,
    tdh.ChangedBy,
    tdh.CreatedOnUtc
FROM TicketDispositionHistory tdh
INNER JOIN Disposition d ON d.pkDispositionID = tdh.DispositionId
WHERE tdh.TicketId = @TicketId
ORDER BY tdh.CreatedOnUtc

-- Offence History
SELECT 'Offence History' AS HistoryType,
    toh.CreatedOnUtc AS EventDate,
    ot.Name AS OffenceType,
    ot.Statute,
    toh.SpeedingGoing,
    toh.SpeedingInA,
    toh.ChangedBy,
    toh.CreatedOnUtc
FROM TicketOffenceHistory toh
INNER JOIN OffenceType ot ON ot.Id = toh.OffenceTypeId
WHERE toh.TicketId = @TicketId
ORDER BY toh.CreatedOnUtc;
```

### View Tickets with Disposition Changes
```sql
-- Find tickets that have disposition history
SELECT 
    t.Id,
    t.TicketNumber,
    t.FileNumber,
    c.FirstName + ' ' + c.LastName AS CustomerName,
    COUNT(tdh.Id) AS DispositionChangeCount,
    MAX(tdh.CreatedOnUtc) AS LastDispositionChange
FROM Ticket t
INNER JOIN Customer c ON c.Id = t.CustomerId
INNER JOIN TicketDispositionHistory tdh ON tdh.TicketId = t.Id
WHERE t.StoreId = 9  -- Garner store
GROUP BY t.Id, t.TicketNumber, t.FileNumber, c.FirstName, c.LastName
ORDER BY LastDispositionChange DESC;
```

### View Speeding Offence History
```sql
-- View tickets with speeding offences
SELECT 
    t.Id,
    t.TicketNumber,
    t.FileNumber,
    ot.Name AS OffenceType,
    toh.SpeedingGoing AS Speed,
    toh.SpeedingInA AS SpeedLimit,
    (toh.SpeedingGoing - toh.SpeedingInA) AS OverLimit,
    toh.CreatedOnUtc
FROM TicketOffenceHistory toh
INNER JOIN Ticket t ON t.Id = toh.TicketId
INNER JOIN OffenceType ot ON ot.Id = toh.OffenceTypeId
WHERE toh.SpeedingGoing IS NOT NULL 
  AND toh.SpeedingInA IS NOT NULL
  AND t.StoreId = 9
ORDER BY (toh.SpeedingGoing - toh.SpeedingInA) DESC;
```

## Benefits

### 1. Audit Trail
- Complete history of all changes to ticket attributes
- Track who made changes and when
- Useful for legal/compliance requirements

### 2. Analysis
- Identify patterns in charge reductions
- Track court appearance frequency
- Analyze disposition outcomes

### 3. Reporting
- Generate reports on case progression
- Show timeline of events for clients
- Track success rates by offence type

### 4. Data Integrity
- Prevents loss of historical information
- Maintains record even if current ticket data changes
- CASCADE DELETE ensures cleanup when tickets are deleted

## Important Notes

### Automatic Cleanup
All history tables use `ON DELETE CASCADE` on the TicketId foreign key. This means:
- ✅ When a ticket is deleted, all its history records are automatically deleted
- ✅ No orphaned history records
- ⚠️ History is permanently lost when ticket is deleted

### Initial Import
During the initial HTA import:
- All tickets get at least one court history entry (if court date exists)
- Tickets with dispositions get one disposition history entry
- Tickets with offence types get one offence history entry
- ChangedBy is set to "HTA Data Import" for all initial entries

### Future Updates
When tickets are updated in the application (not during import):
- New history entries should be created for each change
- ChangedBy should be set to the username of who made the change
- Notes can be added to explain the change

### Performance
History tables are indexed on:
- `TicketId` - Fast lookups by ticket
- `CreatedOnUtc` - Fast chronological queries
- Primary key `Id` - Standard performance

## Files

| File | Purpose |
|------|---------|
| [History_Tables_Setup.sql](History_Tables_Setup.sql) | SQL script to create history tables |
| [HTADataImporter.cs](HTADataImporter.cs) | Updated import code with history tracking |
| HISTORY_TRACKING_IMPLEMENTATION.md | This documentation |

## Testing Checklist

- [ ] Run History_Tables_Setup.sql to create tables
- [ ] Verify tables exist with correct schema
- [ ] Run HTADataImporter with test data
- [ ] Verify court history records created
- [ ] Verify disposition history records created (for tickets with dispositions)
- [ ] Verify offence history records created (for tickets with offences)
- [ ] Test sample queries to view history
- [ ] Verify CASCADE DELETE works (delete a ticket and check history is deleted)

## Troubleshooting

### History Records Not Created
- Ensure History_Tables_Setup.sql was run successfully
- Check that foreign keys (DispositionId, OffenceTypeId, CourtId) are valid
- Verify the importer is not in DryRun mode

### Foreign Key Errors
- Ensure master data setup was run first (Option 1)
- Verify Disposition table has matching records
- Verify OffenceType table has matching records for StoreId 9
- Verify CourtLocation table has matching records for StoreId 9

### Missing History Data
- History is only created if the source data contains the information
- Tickets without dispositions won't have disposition history
- Tickets without mapped offence types won't have offence history
- All tickets should have at least one court history entry

## Future Enhancements

### Possible Additions
1. **TicketFinancialHistory** - Track payment/refund changes
2. **TicketStatusHistory** - Track status changes (Open/Closed/Pending)
3. **TicketNoteHistory** - Track note additions/changes
4. **Change Notifications** - Email alerts when critical changes occur
5. **History Comparison** - UI to compare before/after states

### Application Integration
- Build UI to display timeline of changes
- Add ability to "undo" changes by reverting to historical state
- Generate PDF reports with complete history
- Export history to Excel for analysis
