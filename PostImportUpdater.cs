using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;

namespace HTADataImport
{
    public class PostImportUpdater
    {
        private readonly string _connectionString;
        private readonly bool _dryRun;

        public PostImportUpdater(string connectionString, bool dryRun = true)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _dryRun = dryRun;
        }

        public PostImportResult UpdateImportedData()
        {
            var result = new PostImportResult();

            try
            {
                Console.WriteLine("🔄 Starting post-import updates...\n");

                using var connection = new SqlConnection(_connectionString);
                connection.Open();

                // Step 1: Create mapping table if it doesn't exist
                Console.WriteLine("📋 Ensuring ImportMapping table exists...");
                CreateMappingTable(connection);
                Console.WriteLine();

                // Step 2: Get mapping statistics
                Console.WriteLine("📊 Analyzing imported data...");
                var stats = GetImportStatistics(connection);
                Console.WriteLine($"   • Source records: {stats.SourceRecords}");
                Console.WriteLine($"   • Imported tickets: {stats.ImportedTickets}");
                Console.WriteLine($"   • Imported customers: {stats.ImportedCustomers}\n");

                // Step 3: Populate mapping table
                Console.WriteLine("🔗 Creating mappings...");
                var mappedRecords = PopulateMappingTable(connection, result);
                Console.WriteLine($"   ✓ Mapped {mappedRecords} records\n");

                // Step 4: Add ID columns to TempAllClientInfo
                Console.WriteLine("🔧 Adding ID columns to TempAllClientInfo...");
                AddIdColumnsToSourceTable(connection);
                Console.WriteLine($"   ✓ Columns ready\n");

                // Step 5: Update TempAllClientInfo with imported IDs
                Console.WriteLine("🔄 Updating TempAllClientInfo with imported IDs...");
                var updatedSourceRecords = UpdateSourceTableWithIds(connection, result);
                Console.WriteLine($"   ✓ Updated {updatedSourceRecords} source records\n");

                // Step 6: Update Customer CreatedOnUtc
                Console.WriteLine("📅 Updating Customer.CreatedOnUtc with IntakeDate...");
                var updatedCustomers = UpdateCustomerCreatedDate(connection, result);
                Console.WriteLine($"   ✓ Updated {updatedCustomers} customer records\n");

                // Step 7: Verify updates
                Console.WriteLine("✅ Verifying updates...");
                var verifyStats = VerifyUpdates(connection);
                Console.WriteLine($"   • Matched dates: {verifyStats.MatchedDates}");
                Console.WriteLine($"   • Mismatched dates: {verifyStats.MismatchedDates}");
                Console.WriteLine($"   • Unmapped tickets: {verifyStats.UnmappedTickets}");
                Console.WriteLine($"   • Source records updated: {verifyStats.SourceRecordsWithIds}\n");

                result.MappedRecords = mappedRecords;
                result.UpdatedCustomers = updatedCustomers;
                result.UpdatedSourceRecords = updatedSourceRecords;
                result.Success = true;

                return result;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Post-import update failed: {ex.Message}");
                result.Success = false;
                throw;
            }
        }

        private void CreateMappingTable(SqlConnection connection)
        {
            // Always create the table (if not exists) - even in dry run mode
            // This ensures the table exists for subsequent operations
            var sql = @"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ImportMapping')
                BEGIN
                    CREATE TABLE [dbo].[ImportMapping](
                        [Id] INT IDENTITY(1,1) PRIMARY KEY,
                        [SourcePOT] NVARCHAR(50),
                        [TicketId] INT,
                        [CustomerId] INT,
                        [SourceIntakeDate] DATETIME,
                        [SourceFirstName] NVARCHAR(255),
                        [SourceLastName] NVARCHAR(255),
                        [SourceAddress] NVARCHAR(255),
                        [ImportedOnUtc] DATETIME DEFAULT GETUTCDATE(),
                        
                        CONSTRAINT FK_ImportMapping_Ticket FOREIGN KEY (TicketId) 
                            REFERENCES [dbo].[Ticket](Id),
                        CONSTRAINT FK_ImportMapping_Customer FOREIGN KEY (CustomerId) 
                            REFERENCES [dbo].[Customer](Id)
                    );
                    
                    CREATE INDEX IX_ImportMapping_SourcePOT ON [dbo].[ImportMapping](SourcePOT);
                    CREATE INDEX IX_ImportMapping_TicketId ON [dbo].[ImportMapping](TicketId);
                    CREATE INDEX IX_ImportMapping_CustomerId ON [dbo].[ImportMapping](CustomerId);
                END";

            using var cmd = new SqlCommand(sql, connection);
            cmd.ExecuteNonQuery();

            // Check if table was created or already existed
            var checkSql = "SELECT COUNT(*) FROM sys.tables WHERE name = 'ImportMapping'";
            using var checkCmd = new SqlCommand(checkSql, connection);
            var exists = (int)checkCmd.ExecuteScalar() > 0;
            
            if (exists)
            {
                Console.WriteLine($"   ✓ ImportMapping table ready");
            }
        }

        private ImportStatistics GetImportStatistics(SqlConnection connection)
        {
            var stats = new ImportStatistics();

            // Count source records
            var sql = @"
                SELECT COUNT(*) 
                FROM [dbo].[TempAllClientInfo] 
                WHERE POT IS NOT NULL AND POT NOT IN ('--', '')";
            
            using (var cmd = new SqlCommand(sql, connection))
            {
                stats.SourceRecords = (int)cmd.ExecuteScalar();
            }

            // Count imported tickets
            sql = "SELECT COUNT(*) FROM [dbo].[Ticket] WHERE IsImported = 1";
            using (var cmd = new SqlCommand(sql, connection))
            {
                stats.ImportedTickets = (int)cmd.ExecuteScalar();
            }

            // Count imported customers (check if ImportedFromHTA column exists)
            bool hasImportedFromHTAColumn = CheckColumnExists(connection, "Customer", "ImportedFromHTA");
            
            if (hasImportedFromHTAColumn)
            {
                sql = "SELECT COUNT(*) FROM [dbo].[Customer] WHERE ImportedFromHTA = 1";
            }
            else
            {
                // Count customers linked to imported tickets if column doesn't exist
                sql = @"SELECT COUNT(DISTINCT ticket.CustomerId)
                       FROM [dbo].[Ticket] ticket
                       WHERE ticket.IsImported = 1";
            }
            
            using (var cmd = new SqlCommand(sql, connection))
            {
                stats.ImportedCustomers = (int)cmd.ExecuteScalar();
            }

            return stats;
        }

        private bool CheckColumnExists(SqlConnection connection, string tableName, string columnName)
        {
            var sql = @"
                SELECT COUNT(*)
                FROM sys.columns 
                WHERE object_id = OBJECT_ID(@TableName) 
                AND name = @ColumnName";

            using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@TableName", $"dbo.{tableName}");
            cmd.Parameters.AddWithValue("@ColumnName", columnName);
            
            var count = (int)cmd.ExecuteScalar();
            return count > 0;
        }

        private int PopulateMappingTable(SqlConnection connection, PostImportResult result)
        {
            if (_dryRun)
            {
                // Count how many would be mapped
                var countSql = @"
                    SELECT COUNT(*)
                    FROM [dbo].[TempAllClientInfo] t
                    INNER JOIN [dbo].[Ticket] ticket 
                        ON ticket.TicketNumber = t.POT 
                        AND ticket.IsImported = 1
                    WHERE t.POT IS NOT NULL AND t.POT NOT IN ('--', '')";

                using var countCmd = new SqlCommand(countSql, connection);
                var count = (int)countCmd.ExecuteScalar();
                Console.WriteLine($"   [DRY RUN] Would map {count} records");
                return count;
            }

            var sql = @"
                INSERT INTO [dbo].[ImportMapping] 
                    (SourcePOT, TicketId, CustomerId, SourceIntakeDate, SourceFirstName, SourceLastName, SourceAddress)
                SELECT 
                    t.POT,
                    ticket.Id,
                    ticket.CustomerId,
                    t.IntakeDate,
                    t.[First Name],
                    t.Lastname,
                    t.Address
                FROM [dbo].[TempAllClientInfo] t
                INNER JOIN [dbo].[Ticket] ticket 
                    ON ticket.TicketNumber = t.POT 
                    AND ticket.IsImported = 1
                WHERE t.POT IS NOT NULL 
                    AND t.POT NOT IN ('--', '')
                    AND NOT EXISTS (
                        SELECT 1 FROM [dbo].[ImportMapping] m 
                        WHERE m.SourcePOT = t.POT AND m.TicketId = ticket.Id
                    )";

            using var cmd = new SqlCommand(sql, connection);
            var rowsAffected = cmd.ExecuteNonQuery();

            // Check for unmapped records
            var unmappedSql = @"
                SELECT COUNT(*)
                FROM [dbo].[TempAllClientInfo] t
                LEFT JOIN [dbo].[Ticket] ticket 
                    ON ticket.TicketNumber = t.POT AND ticket.IsImported = 1
                WHERE ticket.Id IS NULL 
                    AND t.POT IS NOT NULL 
                    AND t.POT NOT IN ('--', '')";

            using var unmappedCmd = new SqlCommand(unmappedSql, connection);
            var unmapped = (int)unmappedCmd.ExecuteScalar();

            if (unmapped > 0)
            {
                result.Warnings.Add($"{unmapped} source records could not be mapped to imported tickets");
            }

            return rowsAffected;
        }

        private void AddIdColumnsToSourceTable(SqlConnection connection)
        {
            // Add TicketId column if it doesn't exist
            var addTicketIdSql = @"
                IF NOT EXISTS (SELECT * FROM sys.columns 
                              WHERE object_id = OBJECT_ID('dbo.TempAllClientInfo') 
                              AND name = 'ImportedTicketId')
                BEGIN
                    ALTER TABLE [dbo].[TempAllClientInfo]
                    ADD [ImportedTicketId] INT NULL
                END";

            // Add CustomerId column if it doesn't exist
            var addCustomerIdSql = @"
                IF NOT EXISTS (SELECT * FROM sys.columns 
                              WHERE object_id = OBJECT_ID('dbo.TempAllClientInfo') 
                              AND name = 'ImportedCustomerId')
                BEGIN
                    ALTER TABLE [dbo].[TempAllClientInfo]
                    ADD [ImportedCustomerId] INT NULL
                END";

            if (!_dryRun)
            {
                using (var cmd = new SqlCommand(addTicketIdSql, connection))
                {
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = new SqlCommand(addCustomerIdSql, connection))
                {
                    cmd.ExecuteNonQuery();
                }
            }

            // Verify columns exist or would be created
            var checkSql = @"
                SELECT 
                    CASE WHEN EXISTS (SELECT * FROM sys.columns 
                                     WHERE object_id = OBJECT_ID('dbo.TempAllClientInfo') 
                                     AND name = 'ImportedTicketId') THEN 1 ELSE 0 END AS HasTicketId,
                    CASE WHEN EXISTS (SELECT * FROM sys.columns 
                                     WHERE object_id = OBJECT_ID('dbo.TempAllClientInfo') 
                                     AND name = 'ImportedCustomerId') THEN 1 ELSE 0 END AS HasCustomerId";

            using var checkCmd = new SqlCommand(checkSql, connection);
            using var reader = checkCmd.ExecuteReader();
            if (reader.Read())
            {
                var hasTicketId = reader.GetInt32(0) == 1;
                var hasCustomerId = reader.GetInt32(1) == 1;

                if (_dryRun)
                {
                    if (!hasTicketId || !hasCustomerId)
                    {
                        Console.WriteLine($"   [DRY RUN] Would add columns: " +
                            $"{(!hasTicketId ? "ImportedTicketId " : "")}" +
                            $"{(!hasCustomerId ? "ImportedCustomerId" : "")}");
                    }
                    else
                    {
                        Console.WriteLine($"   [DRY RUN] Columns already exist");
                    }
                }
            }
        }

        private int UpdateSourceTableWithIds(SqlConnection connection, PostImportResult result)
        {
            if (_dryRun)
            {
                // Count how many would be updated
                var countSql = @"
                    SELECT COUNT(*)
                    FROM [dbo].[TempAllClientInfo] t
                    INNER JOIN [dbo].[ImportMapping] m ON m.SourcePOT = t.POT
                    WHERE (t.ImportedTicketId IS NULL OR t.ImportedTicketId != m.TicketId 
                           OR t.ImportedCustomerId IS NULL OR t.ImportedCustomerId != m.CustomerId)";

                using var countCmd = new SqlCommand(countSql, connection);
                var count = (int)countCmd.ExecuteScalar();
                Console.WriteLine($"   [DRY RUN] Would update {count} source records");
                return count;
            }

            var sql = @"
                UPDATE t
                SET 
                    t.ImportedTicketId = m.TicketId,
                    t.ImportedCustomerId = m.CustomerId
                FROM [dbo].[TempAllClientInfo] t
                INNER JOIN [dbo].[ImportMapping] m ON m.SourcePOT = t.POT";

            using var cmd = new SqlCommand(sql, connection);
            return cmd.ExecuteNonQuery();
        }

        private int UpdateCustomerCreatedDate(SqlConnection connection, PostImportResult result)
        {
            // Check if ImportedFromHTA column exists
            bool hasImportedFromHTAColumn = CheckColumnExists(connection, "Customer", "ImportedFromHTA");

            if (_dryRun)
            {
                // Count how many would be updated
                var countSql = hasImportedFromHTAColumn
                    ? @"SELECT COUNT(*)
                        FROM [dbo].[Customer] c
                        INNER JOIN [dbo].[ImportMapping] m ON m.CustomerId = c.Id
                        WHERE m.SourceIntakeDate IS NOT NULL
                            AND c.ImportedFromHTA = 1
                            AND c.CreatedOnUtc != m.SourceIntakeDate"
                    : @"SELECT COUNT(*)
                        FROM [dbo].[Customer] c
                        INNER JOIN [dbo].[ImportMapping] m ON m.CustomerId = c.Id
                        WHERE m.SourceIntakeDate IS NOT NULL
                            AND c.CreatedOnUtc != m.SourceIntakeDate";

                using var countCmd = new SqlCommand(countSql, connection);
                var count = (int)countCmd.ExecuteScalar();
                Console.WriteLine($"   [DRY RUN] Would update {count} customer records");
                
                if (!hasImportedFromHTAColumn)
                {
                    Console.WriteLine($"   [WARNING] ImportedFromHTA column not found - updating all customers from mapping");
                }
                
                return count;
            }

            var sql = hasImportedFromHTAColumn
                ? @"UPDATE c
                    SET c.CreatedOnUtc = m.SourceIntakeDate
                    FROM [dbo].[Customer] c
                    INNER JOIN [dbo].[ImportMapping] m ON m.CustomerId = c.Id
                    WHERE m.SourceIntakeDate IS NOT NULL
                        AND c.ImportedFromHTA = 1"
                : @"UPDATE c
                    SET c.CreatedOnUtc = m.SourceIntakeDate
                    FROM [dbo].[Customer] c
                    INNER JOIN [dbo].[ImportMapping] m ON m.CustomerId = c.Id
                    WHERE m.SourceIntakeDate IS NOT NULL";

            if (!hasImportedFromHTAColumn)
            {
                Console.WriteLine($"   [INFO] ImportedFromHTA column not found - updating all customers from mapping");
            }

            using var cmd = new SqlCommand(sql, connection);
            return cmd.ExecuteNonQuery();
        }

        private VerificationStatistics VerifyUpdates(SqlConnection connection)
        {
            var stats = new VerificationStatistics();

            // Count matched dates
            var sql = @"
                SELECT COUNT(*)
                FROM [dbo].[Customer] c
                INNER JOIN [dbo].[ImportMapping] m ON m.CustomerId = c.Id
                WHERE c.CreatedOnUtc = m.SourceIntakeDate";

            using (var cmd = new SqlCommand(sql, connection))
            {
                stats.MatchedDates = (int)cmd.ExecuteScalar();
            }

            // Count mismatched dates
            sql = @"
                SELECT COUNT(*)
                FROM [dbo].[Customer] c
                INNER JOIN [dbo].[ImportMapping] m ON m.CustomerId = c.Id
                WHERE c.CreatedOnUtc != m.SourceIntakeDate";

            using (var cmd = new SqlCommand(sql, connection))
            {
                stats.MismatchedDates = (int)cmd.ExecuteScalar();
            }

            // Count unmapped tickets
            sql = @"
                SELECT COUNT(*)
                FROM [dbo].[TempAllClientInfo] t
                LEFT JOIN [dbo].[Ticket] ticket 
                    ON ticket.TicketNumber = t.POT AND ticket.IsImported = 1
                WHERE ticket.Id IS NULL 
                    AND t.POT IS NOT NULL 
                    AND t.POT NOT IN ('--', '')";

            using (var cmd = new SqlCommand(sql, connection))
            {
                stats.UnmappedTickets = (int)cmd.ExecuteScalar();
            }

            // Count source records with IDs
            sql = @"
                SELECT COUNT(*)
                FROM [dbo].[TempAllClientInfo]
                WHERE ImportedTicketId IS NOT NULL AND ImportedCustomerId IS NOT NULL";

            using (var cmd = new SqlCommand(sql, connection))
            {
                stats.SourceRecordsWithIds = (int)cmd.ExecuteScalar();
            }

            return stats;
        }
    }

    public class PostImportResult
    {
        public bool Success { get; set; }
        public int MappedRecords { get; set; }
        public int UpdatedCustomers { get; set; }
        public int UpdatedSourceRecords { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class ImportStatistics
    {
        public int SourceRecords { get; set; }
        public int ImportedTickets { get; set; }
        public int ImportedCustomers { get; set; }
    }

    public class VerificationStatistics
    {
        public int MatchedDates { get; set; }
        public int MismatchedDates { get; set; }
        public int UnmappedTickets { get; set; }
        public int SourceRecordsWithIds { get; set; }
    }
}
