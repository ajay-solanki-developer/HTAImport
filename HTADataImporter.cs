using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.SqlClient;
using HTADataImport.Models;

namespace HTADataImport
{
    public class HTADataImporter
    {
        private readonly string _connectionString;
        private readonly bool _dryRun;
        private readonly int _storeId;
        private readonly string _firmName;
        private readonly string? _filterCsvPath;
        private HashSet<string>? _ticketFilter = null;
        
        // Lookup caches
        private Dictionary<string, int> _courtLocationCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _offenceTypeCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _stateProvinceCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int? _canadaCountryId = null;
        private int? _defaultSourceId = null;
        private int? _clientRoleId = null;
        
        // Track unmapped items for reporting
        private HashSet<string> _unmappedCourts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _unmappedOffences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public HTADataImporter(string connectionString, int storeId = 1, string firmName = "HTA Import", string? filterCsvPath = null, bool dryRun = true)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _storeId = storeId;
            _firmName = firmName ?? "HTA Import";
            _filterCsvPath = filterCsvPath;
            _dryRun = dryRun;
        }

        public ImportResult Import()
        {
            var result = new ImportResult();

            try
            {
                // Step 0: Load ticket filter if provided
                if (!string.IsNullOrEmpty(_filterCsvPath) && File.Exists(_filterCsvPath))
                {
                    Console.WriteLine("📋 Loading ticket filter from CSV...");
                    LoadTicketFilter();
                    Console.WriteLine($"   ✓ Loaded {_ticketFilter?.Count ?? 0} ticket numbers to filter\n");
                }

                // Step 1: Read data from SQL Server table
                Console.WriteLine("📄 Reading data from TempAllClientInfo table...");
                var records = ReadDataFromTable();
                Console.WriteLine($"   ✓ Read {records.Count} records from database\n");

                // Step 2: Test database connection
                Console.WriteLine("🔌 Testing database connection...");
                if (!TestConnection())
                {
                    result.Errors.Add("Failed to connect to database");
                    return result;
                }
                Console.WriteLine("   ✓ Database connection successful\n");

                // Step 2.5: Initialize lookup caches
                Console.WriteLine("📚 Loading reference data...");
                InitializeLookupCaches();
                Console.WriteLine($"   ✓ Loaded {_courtLocationCache.Count} courts, {_offenceTypeCache.Count} offence types, {_stateProvinceCache.Count} provinces\n");

                // Step 3: Check database schema
                Console.WriteLine("🔍 Checking database schema...");
                var customerSchema = GetCustomerTableSchema();
                Console.WriteLine($"   ✓ Found {customerSchema.Count} columns in Customer table");
                
                // Step 4: Import customers
                Console.WriteLine("\n👥 Importing customers...");
                var customerIds = ImportCustomers(records, customerSchema, result);
                result.CustomersImported = customerIds.Count;
                Console.WriteLine($"   ✓ Processed {result.CustomersImported} unique customers\n");

                // Step 5: Update emails with test emails
                if (!_dryRun && result.CustomersImported > 0)
                {
                    Console.WriteLine("📧 Generating test emails for imported customers...");
                    UpdateCustomerEmails(result);
                    Console.WriteLine($"   ✓ Updated {result.CustomersImported} customer emails\n");
                }

                // Step 5.5: Assign Client role to imported customers
                if (!_dryRun && result.CustomersImported > 0)
                {
                    Console.WriteLine("👤 Assigning Client role to imported customers...");
                    AssignClientRole(result);
                    Console.WriteLine();
                }

                // Step 6: Import tickets
                Console.WriteLine("🎫 Importing tickets...");
                ImportTickets(records, customerIds, result);
                Console.WriteLine($"   ✓ Processed {result.TicketsImported} tickets\n");

                // Step 7: Report unmapped data
                ReportUnmappedData(result);

                return result;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Import failed: {ex.Message}");
                throw;
            }
        }

        private void LoadTicketFilter()
        {
            _ticketFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            try
            {
                var lines = File.ReadAllLines(_filterCsvPath!);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    // Skip empty lines and header lines
                    if (!string.IsNullOrWhiteSpace(trimmed) && 
                        !trimmed.Equals("POT", StringComparison.OrdinalIgnoreCase) &&
                        !trimmed.Equals("TicketNumber", StringComparison.OrdinalIgnoreCase))
                    {
                        _ticketFilter.Add(trimmed);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠ Warning loading filter: {ex.Message}");
                _ticketFilter = null;
            }
        }

        private List<HTARecord> ReadDataFromTable()
        {
            var records = new List<HTARecord>();
            
            try
            {
                using var connection = new SqlConnection(_connectionString);
                connection.Open();

                // Build WHERE clause for ticket filter
                var whereClause = "";
                if (_ticketFilter != null && _ticketFilter.Count > 0)
                {
                    var quotedTickets = string.Join(",", _ticketFilter.Select(t => $"'{t.Replace("'", "''")}'")); 
                    whereClause = $" WHERE POT IN ({quotedTickets})";
                }

                var sql = $@"SELECT 
                    IntakeDate, [First Name], Lastname, Address, City, Prov, Postal, 
                    ynDiscrete, homephone, businessphone, Ext, Cell, Fax, Gender, 
                    tblClient_Notes, POT, ICON, TicketDate, Intake, SectionNumber, 
                    tblOffenseWording_Description, SpeedingGoing, SpeedingInA, BadgeNumber, 
                    CourtName, [1stApp], Rm, Time, tblDisposition_Description, Name, 
                    tblGuarantee_Description, WePay, HePays, Fee, GST, Total, 
                    TotalPayments, Balance, Language, SpecialInstructions, 
                    tblTicket_Notes, DateDisclosureRequested, DateDisclosureReceived
                FROM [dbo].[TempAllClientInfo]{whereClause}";

                using var cmd = new SqlCommand(sql, connection);
                using var reader = cmd.ExecuteReader();
                
                while (reader.Read())
                {
                    var record = new HTARecord
                    {
                        IntakeDate = GetSafeString(reader, "IntakeDate"),
                        FirstName = GetSafeString(reader, "First Name"),
                        LastName = GetSafeString(reader, "Lastname"),
                        Address = GetSafeString(reader, "Address"),
                        City = GetSafeString(reader, "City"),
                        Prov = GetSafeString(reader, "Prov"),
                        Postal = GetSafeString(reader, "Postal"),
                        YnDiscrete = GetSafeString(reader, "ynDiscrete"),
                        HomePhone = GetSafeString(reader, "homephone"),
                        BusinessPhone = GetSafeString(reader, "businessphone"),
                        Ext = GetSafeString(reader, "Ext"),
                        Cell = GetSafeString(reader, "Cell"),
                        Fax = GetSafeString(reader, "Fax"),
                        Gender = GetSafeString(reader, "Gender"),
                        Notes = GetSafeString(reader, "tblClient_Notes"),
                        POT = GetSafeString(reader, "POT"),
                        ICON = GetSafeString(reader, "ICON"),
                        TicketDate = GetSafeString(reader, "TicketDate"),
                        Intake = GetSafeString(reader, "Intake"),
                        SectionNumber = GetSafeString(reader, "SectionNumber"),
                        OffenseWording = GetSafeString(reader, "tblOffenseWording_Description"),
                        SpeedingGoing = GetSafeString(reader, "SpeedingGoing"),
                        SpeedingInA = GetSafeString(reader, "SpeedingInA"),
                        BadgeNumber = GetSafeString(reader, "BadgeNumber"),
                        CourtName = GetSafeString(reader, "CourtName"),
                        FirstApp = GetSafeString(reader, "1stApp"),
                        Rm = GetSafeString(reader, "Rm"),
                        Time = GetSafeString(reader, "Time"),
                        Disposition = GetSafeString(reader, "tblDisposition_Description"),
                        Name = GetSafeString(reader, "Name"),
                        Guarantee = GetSafeString(reader, "tblGuarantee_Description"),
                        WePay = GetSafeString(reader, "WePay"),
                        Fee = GetSafeString(reader, "Fee"),
                        Tax = GetSafeString(reader, "GST"),
                        Total = GetSafeString(reader, "Total"),
                        Paid = GetSafeString(reader, "TotalPayments"),
                        Balance = GetSafeString(reader, "Balance"),
                        Language = GetSafeString(reader, "Language"),
                        SpecialInstructions = GetSafeString(reader, "SpecialInstructions"),
                        TicketNotes = GetSafeString(reader, "tblTicket_Notes"),
                        DateDisclosureRequested = GetSafeString(reader, "DateDisclosureRequested"),
                        DateDisclosureReceived = GetSafeString(reader, "DateDisclosureReceived"),
                        Fine = GetSafeString(reader, "HePays")  // HePays maps to Fine
                    };
                    
                    records.Add(record);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ✗ Failed to read data: {ex.Message}");
                throw;
            }

            return records;
        }

        private string? GetSafeString(SqlDataReader reader, string columnName)
        {
            try
            {
                var ordinal = reader.GetOrdinal(columnName);
                if (reader.IsDBNull(ordinal))
                    return null;
                
                var value = reader.GetValue(ordinal);
                
                // Handle datetime columns
                if (value is DateTime dt)
                    return dt.ToString("yyyy-MM-dd HH:mm:ss");
                
                // Handle boolean columns
                if (value is bool b)
                    return b.ToString();
                    
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private bool TestConnection()
        {
            try
            {
                using var connection = new SqlConnection(_connectionString);
                connection.Open();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ✗ Connection failed: {ex.Message}");
                return false;
            }
        }

        private void InitializeLookupCaches()
        {
            try
            {
                using var connection = new SqlConnection(_connectionString);
                connection.Open();

                // Load Canada Country ID
                var countryCmd = new SqlCommand("SELECT TOP 1 Id FROM Country WHERE Name LIKE '%Canada%'", connection);
                var countryResult = countryCmd.ExecuteScalar();
                _canadaCountryId = countryResult != null ? (int)countryResult : (int?)null;

                // Load State/Provinces for Canada
                if (_canadaCountryId.HasValue)
                {
                    var stateCmd = new SqlCommand($"SELECT Id, Name, Abbreviation FROM StateProvince WHERE CountryId = {_canadaCountryId.Value}", connection);
                    using (var stateReader = stateCmd.ExecuteReader())
                    {
                        while (stateReader.Read())
                        {
                            var id = stateReader.GetInt32(0);
                            var name = stateReader.GetString(1);
                            var abbr = stateReader.IsDBNull(2) ? null : stateReader.GetString(2);
                            
                            _stateProvinceCache[name] = id;
                            if (!string.IsNullOrEmpty(abbr))
                                _stateProvinceCache[abbr] = id;
                        }
                    }
                }

                // Load Court Locations
                var courtCmd = new SqlCommand($"SELECT Id, Name, IconCode, CourtJurisdictionId FROM CourtLocation WHERE StoreId = {_storeId} AND IsActive = 1", connection);
                using (var courtReader = courtCmd.ExecuteReader())
                {
                    while (courtReader.Read())
                    {
                        var id = courtReader.GetInt32(0);
                        var name = courtReader.GetString(1);
                        var iconCode = courtReader.IsDBNull(2) ? null : courtReader.GetString(2);
                        // Note: CourtJurisdictionId at index 3 is available if needed
                        
                        _courtLocationCache[name] = id;
                        if (!string.IsNullOrEmpty(iconCode))
                            _courtLocationCache[iconCode] = id;
                    }
                }

                // Load Offence Types
                var offenceCmd = new SqlCommand($"SELECT Id, Name, Statute FROM OffenceType WHERE StoreId = {_storeId} AND IsActive = 1", connection);
                using (var offenceReader = offenceCmd.ExecuteReader())
                {
                    while (offenceReader.Read())
                    {
                        var id = offenceReader.GetInt32(0);
                        var name = offenceReader.GetString(1);
                        var statute = offenceReader.IsDBNull(2) ? null : offenceReader.GetString(2);
                        
                        // Store by name
                        _offenceTypeCache[name] = id;
                        
                        // Also store by statute/section number if available
                        if (!string.IsNullOrWhiteSpace(statute))
                        {
                            _offenceTypeCache[statute.Trim()] = id;
                        }
                    }
                }

                // Load or create default Source
                var sourceCmd = new SqlCommand($"SELECT TOP 1 Id FROM Source WHERE StoreId = {_storeId} AND Name = 'Data Import' AND IsActive = 1", connection);
                var sourceResult = sourceCmd.ExecuteScalar();
                if (sourceResult != null)
                {
                    _defaultSourceId = (int)sourceResult;
                }
                else if (!_dryRun)
                {
                    // Create default source
                    var createSourceCmd = new SqlCommand(
                        $"INSERT INTO Source (StoreId, Name, Description, IsActive, CreatedOnUtc) " +
                        $"OUTPUT INSERTED.Id " +
                        $"VALUES ({_storeId}, 'Data Import', 'Records imported from legacy system', 1, GETUTCDATE())", 
                        connection);
                    _defaultSourceId = (int)createSourceCmd.ExecuteScalar();
                }

                // Load Client role ID
                var roleCmd = new SqlCommand("SELECT TOP 1 Id FROM CustomerRole WHERE Name = 'Client' OR Name = 'Clients' OR SystemName = 'Client' OR SystemName = 'Clients'", connection);
                var roleResult = roleCmd.ExecuteScalar();
                if (roleResult != null)
                {
                    _clientRoleId = (int)roleResult;
                    Console.WriteLine($"   ✓ Found Client role (ID: {_clientRoleId})");
                }
                else
                {
                    Console.WriteLine("   ⚠ Warning: Client role not found in CustomerRole table");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠ Warning loading reference data: {ex.Message}");
            }
        }

        private int? GetCourtLocationId(string? courtName, string? iconCode)
        {
            if (!string.IsNullOrWhiteSpace(iconCode) && _courtLocationCache.TryGetValue(iconCode, out int iconId))
                return iconId;
            
            if (!string.IsNullOrWhiteSpace(courtName) && _courtLocationCache.TryGetValue(courtName, out int nameId))
                return nameId;
            
            // Track unmapped court for reporting
            var courtDesc = $"ICON: '{iconCode ?? "(none)"}', Name: '{courtName ?? "(none)"}'";
            _unmappedCourts.Add(courtDesc);
            
            return null;
        }

        private int? GetOffenceTypeId(string? sectionNumber, string? offenceDescription)
        {
            // First try to match by section number (Statute field)
            if (!string.IsNullOrWhiteSpace(sectionNumber))
            {
                var cleanSection = sectionNumber.Trim();
                if (_offenceTypeCache.TryGetValue(cleanSection, out int sectionId))
                    return sectionId;
            }
            
            // Fall back to offense description if section number doesn't match
            if (!string.IsNullOrWhiteSpace(offenceDescription))
            {
                // Try exact match
                if (_offenceTypeCache.TryGetValue(offenceDescription, out int id))
                    return id;
                
                // Try partial match
                var match = _offenceTypeCache.FirstOrDefault(kvp => 
                    offenceDescription.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.Contains(offenceDescription, StringComparison.OrdinalIgnoreCase));
                
                if (!match.Equals(default(KeyValuePair<string, int>)))
                    return match.Value;
            }
            
            // Track unmapped offence for reporting
            var offenceDesc = $"Section: '{sectionNumber ?? "(none)"}', Description: '{offenceDescription ?? "(none)"}'";
            _unmappedOffences.Add(offenceDesc);
            
            return null;
        }

        private int? GetStateProvinceId(string? province)
        {
            if (string.IsNullOrWhiteSpace(province))
                return null;
            
            if (_stateProvinceCache.TryGetValue(province, out int id))
                return id;
            
            return null;
        }

        private List<ColumnInfo> GetCustomerTableSchema()
        {
            var columns = new List<ColumnInfo>();
            
            try
            {
                using var connection = new SqlConnection(_connectionString);
                connection.Open();

                var sql = @"
                    SELECT 
                        COLUMN_NAME,
                        DATA_TYPE,
                        IS_NULLABLE,
                        COLUMN_DEFAULT
                    FROM INFORMATION_SCHEMA.COLUMNS 
                    WHERE TABLE_NAME = 'Customer'
                    ORDER BY ORDINAL_POSITION";

                using var cmd = new SqlCommand(sql, connection);
                using var reader = cmd.ExecuteReader();
                
                while (reader.Read())
                {
                    columns.Add(new ColumnInfo
                    {
                        ColumnName = reader.GetString(0),
                        DataType = reader.GetString(1),
                        IsNullable = reader.GetString(2) == "YES",
                        ColumnDefault = reader.IsDBNull(3) ? null : reader.GetString(3)
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ✗ Failed to get schema: {ex.Message}");
            }

            return columns;
        }

        private Dictionary<string, int> ImportCustomers(List<HTARecord> records, List<ColumnInfo> schema, ImportResult result)
        {
            var customerIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            var uniqueCustomers = records
                .Where(r => !string.IsNullOrWhiteSpace(r.FirstName) || !string.IsNullOrWhiteSpace(r.LastName))
                .GroupBy(r => new { 
                    FirstName = r.FirstName?.Trim() ?? "", 
                    LastName = r.LastName?.Trim() ?? "",
                    Address = r.Address?.Trim() ?? ""
                })
                .Select(g => g.First())
                .ToList();

            using var connection = new SqlConnection(_connectionString);
            if (!_dryRun)
                connection.Open();

            int count = 0;
            foreach (var record in uniqueCustomers)
            {
                count++;
                var customerKey = $"{record.FirstName?.Trim()}_{record.LastName?.Trim()}_{record.Address?.Trim()}";
                
                if (string.IsNullOrWhiteSpace(customerKey.Replace("_", "")))
                {
                    result.Warnings.Add($"Skipping customer with no name or address");
                    continue;
                }

                try
                {
                    if (_dryRun)
                    {
                        customerIds[customerKey] = count;
                        if (count <= 5)
                        {
                            Console.WriteLine($"   [DRY RUN] Would import: {record.FirstName} {record.LastName} - {record.City}");
                        }
                        else if (count == 6)
                        {
                            Console.WriteLine($"   [DRY RUN] ... and {uniqueCustomers.Count - 5} more customers");
                        }
                    }
                    else
                    {
                        var customerId = InsertCustomer(connection, record, schema);
                        customerIds[customerKey] = customerId;
                        
                        if (count % 100 == 0)
                        {
                            Console.WriteLine($"   Processing {count}/{uniqueCustomers.Count} customers...");
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Failed to import customer {record.FirstName} {record.LastName}: {ex.Message}");
                }
            }

            return customerIds;
        }

        private int InsertCustomer(SqlConnection connection, HTARecord record, List<ColumnInfo> schema)
        {
            var columns = new List<string>();
            var parameters = new List<string>();
            var values = new Dictionary<string, object?>();

            // Generate a temporary email that will be updated later with the actual ID
            var tempEmail = $"temp-{Guid.NewGuid().ToString().Substring(0, 8)}@test.com";

            // Get foreign key IDs
            var stateProvinceId = GetStateProvinceId(record.Prov) ?? 0;
            var countryId = _canadaCountryId ?? 0;
            var sourceId = _defaultSourceId;

            // Map our data to columns
            var dataMap = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["CustomerGuid"] = Guid.NewGuid(),
                ["FirstName"] = record.FirstName,
                ["LastName"] = record.LastName,
                ["Email"] = tempEmail,  // Temporary email, will be updated after insert
                ["Gender"] = record.Gender,
                ["StreetAddress"] = record.Address,
                ["City"] = record.City,
                ["County"] = record.Prov,
                ["ZipPostalCode"] = record.Postal,
                ["Phone"] = GetPrimaryPhone(record),
                ["Fax"] = record.Fax,
                ["ContactTypeId"] = 1,
                ["EntityTypeId"] = 1,
                ["RegisteredInStoreId"] = _storeId,
                ["CountryId"] = countryId,
                ["StateProvinceId"] = stateProvinceId,
                ["SourceId"] = sourceId,
                ["AdminComment"] = record.Notes,
                ["CreatedOnUtc"] = DateTime.UtcNow,
                ["LastActivityDateUtc"] = DateTime.UtcNow,
                ["Active"] = true,
                ["Deleted"] = false,
                ["IsSystemAccount"] = false,
                ["HstExempt"] = false,
                ["ImportedFromHTA"] = true,
                ["ImportedFromFirm"] = _firmName
            };

            // Process each column in the schema
            foreach (var col in schema)
            {
                // Skip identity columns
                if (col.ColumnName.Equals("Id", StringComparison.OrdinalIgnoreCase))
                    continue;

                object? value = null;

                // Check if we have data for this column
                if (dataMap.ContainsKey(col.ColumnName))
                {
                    value = dataMap[col.ColumnName];
                }
                else
                {
                    // Generate default value based on data type and nullable
                    value = GetDefaultValue(col);
                }

                // Only add if we have a value or it's nullable
                if (value != null || col.IsNullable)
                {
                    columns.Add($"[{col.ColumnName}]");
                    parameters.Add($"@{col.ColumnName}");
                    values[$"@{col.ColumnName}"] = value;
                }
            }

            var sql = $@"
                INSERT INTO [Customer] ({string.Join(", ", columns)})
                OUTPUT INSERTED.Id
                VALUES ({string.Join(", ", parameters)})";

            using var cmd = new SqlCommand(sql, connection);
            
            foreach (var kvp in values)
            {
                cmd.Parameters.AddWithValue(kvp.Key, kvp.Value ?? DBNull.Value);
            }

            return (int)cmd.ExecuteScalar()!;
        }

        private void AssignClientRole(ImportResult result)
        {
            try
            {
                if (!_clientRoleId.HasValue)
                {
                    result.Warnings.Add("Cannot assign Client role: Role ID not found. Please ensure a 'Client' role exists in CustomerRole table.");
                    return;
                }

                using var connection = new SqlConnection(_connectionString);
                connection.Open();

                // Assign Client role to all newly imported customers
                var sql = $@"
                    INSERT INTO [Customer_CustomerRole_Mapping] (Customer_Id, CustomerRole_Id)
                    SELECT c.Id, @RoleId
                    FROM [Customer] c
                    LEFT JOIN [Customer_CustomerRole_Mapping] m ON c.Id = m.Customer_Id AND m.CustomerRole_Id = @RoleId
                    WHERE c.ImportedFromHTA = 1
                    AND c.CreatedOnUtc >= DATEADD(MINUTE, -10, GETUTCDATE())
                    AND m.Customer_Id IS NULL";

                using var cmd = new SqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@RoleId", _clientRoleId.Value);
                var assigned = cmd.ExecuteNonQuery();
                
                Console.WriteLine($"   ✓ Assigned Client role to {assigned} customers");
                
                if (assigned == 0)
                {
                    result.Warnings.Add("No customers were assigned the Client role. They may already have it, or ImportedFromHTA flag may not be set.");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to assign Client role: {ex.Message}");
                Console.WriteLine($"   ✗ Error: {ex.Message}");
            }
        }

        private void UpdateCustomerEmails(ImportResult result)
        {
            try
            {
                using var connection = new SqlConnection(_connectionString);
                connection.Open();

                // Update all customers that have temp emails to use their ID
                var sql = @"
                    UPDATE [Customer]
                    SET Email = 'customer' + CAST(Id AS NVARCHAR(50)) + '@test.com'
                    WHERE Email LIKE 'temp-%@test.com'
                    AND CreatedOnUtc >= DATEADD(MINUTE, -5, GETUTCDATE())";

                using var cmd = new SqlCommand(sql, connection);
                var updated = cmd.ExecuteNonQuery();
                
                if (updated > 0)
                {
                    Console.WriteLine($"   ✓ Updated {updated} email addresses");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to update customer emails: {ex.Message}");
            }
        }

        private object? GetDefaultValue(ColumnInfo column)
        {
            // If it has a database default, skip it
            if (!string.IsNullOrEmpty(column.ColumnDefault))
                return null;

            // If it's nullable, we can use null
            if (column.IsNullable)
                return null;

            // Generate default based on data type
            return column.DataType.ToLower() switch
            {
                "bit" => false,
                "tinyint" or "smallint" or "int" or "bigint" => 0,
                "decimal" or "numeric" or "float" or "real" or "money" or "smallmoney" => 0,
                "datetime" or "datetime2" or "smalldatetime" or "date" => DateTime.UtcNow,
                "time" => TimeSpan.Zero,
                "uniqueidentifier" => Guid.NewGuid(),
                "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" => string.Empty,
                _ => null
            };
        }

        private void ImportTickets(List<HTARecord> records, Dictionary<string, int> customerIds, ImportResult result)
        {
            using var connection = new SqlConnection(_connectionString);
            if (!_dryRun)
                connection.Open();

            int count = 0;
            int skipped = 0;
            
            // Sort records by IntakeDate in ascending order
            var sortedRecords = records
                .OrderBy(r => ParseDate(r.IntakeDate) ?? DateTime.MaxValue)
                .ToList();
            
            // Track file numbers per Year-Month
            var fileNumberCounters = new Dictionary<string, int>();

            foreach (var record in sortedRecords)
            {
                var customerKey = $"{record.FirstName?.Trim()}_{record.LastName?.Trim()}_{record.Address?.Trim()}";
                
                if (!customerIds.TryGetValue(customerKey, out int customerId))
                {
                    skipped++;
                    continue;
                }

                // Generate FileNumber based on IntakeDate pattern: YYYY-MM-XXX
                string fileNumber = GenerateFileNumber(record, fileNumberCounters);

                try
                {
                    if (_dryRun)
                    {
                        count++;
                        if (count <= 5)
                        {
                            Console.WriteLine($"   [DRY RUN] Would import ticket: {record.POT} (File: {fileNumber}) for customer {customerId}");
                        }
                        else if (count == 6)
                        {
                            Console.WriteLine($"   [DRY RUN] ... and {records.Count - 5} more tickets");
                        }
                    }
                    else
                    {
                        var ticketId = InsertTicket(connection, customerId, record, fileNumber);
                        
                        // Create court history entry for initial court date
                        if (ticketId > 0)
                        {
                            InsertTicketCourtHistory(connection, ticketId, record);
                        }
                        
                        count++;
                        
                        if (count % 100 == 0)
                        {
                            Console.WriteLine($"   Processing {count} tickets...");
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Failed to import ticket {record.POT}: {ex.Message}");
                }
            }

            result.TicketsImported = count;
            if (skipped > 0)
            {
                result.Warnings.Add($"Skipped {skipped} tickets without matching customers");
            }
        }

        private string GenerateFileNumber(HTARecord record, Dictionary<string, int> fileNumberCounters)
        {
            // Use IntakeDate to generate file number in format YYYY-MM-XXX
            var intakeDate = ParseDate(record.IntakeDate);
            
            if (intakeDate.HasValue)
            {
                var yearMonth = intakeDate.Value.ToString("yyyy-MM");
                
                // Increment counter for this year-month
                if (!fileNumberCounters.ContainsKey(yearMonth))
                {
                    fileNumberCounters[yearMonth] = 0;
                }
                
                fileNumberCounters[yearMonth]++;
                
                return $"{yearMonth}-{fileNumberCounters[yearMonth]:D3}";
            }
            else
            {
                // Fallback if no intake date - use current date
                var yearMonth = DateTime.Now.ToString("yyyy-MM");
                
                if (!fileNumberCounters.ContainsKey(yearMonth))
                {
                    fileNumberCounters[yearMonth] = 0;
                }
                
                fileNumberCounters[yearMonth]++;
                
                return $"{yearMonth}-{fileNumberCounters[yearMonth]:D3}";
            }
        }

        private int InsertTicket(SqlConnection connection, int customerId, HTARecord record, string fileNumber)
        {
            // Get foreign key IDs
            var courtId = GetCourtLocationId(record.CourtName, record.ICON);
            var offenceTypeId = GetOffenceTypeId(record.SectionNumber, record.OffenseWording);
            
            // Parse section number if available
            int? sectionNumber = null;
            if (!string.IsNullOrWhiteSpace(record.SectionNumber) && int.TryParse(record.SectionNumber, out int secNum))
                sectionNumber = secNum;
            
            // Parse officer badge
            int? officerBadge = null;
            if (!string.IsNullOrWhiteSpace(record.BadgeNumber) && int.TryParse(record.BadgeNumber, out int badge))
                officerBadge = badge;
            
            // Parse court time
            TimeSpan? courtTime = null;
            if (!string.IsNullOrWhiteSpace(record.Time))
            {
                if (DateTime.TryParse(record.Time, out DateTime timeResult))
                    courtTime = timeResult.TimeOfDay;
            }

            var sql = @"
                INSERT INTO [Ticket] (
                    CustomerId, IconId, CourtId, 
                    TicketNumber, FileNumber, OffenceNumber,
                    OffenceDate, DateEntered, DateRetained,
                    CourtDate, CourtRoom, CourtTime,
                    SectionNumber, Wording, OfficerBadge,
                    Fee, FineToPay, Tax, Total, TotalPaid, Balance,
                    Guarantee, Notes, SpecialInstructions,
                    StatusKey, IsImported,
                    Deleted, ClientWantsToAttend, InterpreterNeeded, IsAccident,
                    IsPreTrialNeeded, IsQueued, IsDone
                )
                OUTPUT INSERTED.Id
                VALUES (
                    @CustomerId, @IconId, @CourtId,
                    @TicketNumber, @FileNumber, @OffenceNumber,
                    @OffenceDate, @DateEntered, @DateRetained,
                    @CourtDate, @CourtRoom, @CourtTime,
                    @SectionNumber, @Wording, @OfficerBadge,
                    @Fee, @FineToPay, @Tax, @Total, @TotalPaid, @Balance,
                    @Guarantee, @Notes, @SpecialInstructions,
                    'Imported', 1,
                    0, 0, 0, 0,
                    0, 0, 0
                )";

            using var cmd = new SqlCommand(sql, connection);
            
            // Customer and Court info
            cmd.Parameters.AddWithValue("@CustomerId", customerId);
            cmd.Parameters.AddWithValue("@IconId", (object?)courtId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CourtId", (object?)courtId ?? DBNull.Value);
            
            // Ticket identification - POT is the Ticket Number, FileNumber is generated
            cmd.Parameters.AddWithValue("@TicketNumber", (object?)record.POT ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FileNumber", fileNumber);
            cmd.Parameters.AddWithValue("@OffenceNumber", (object?)offenceTypeId ?? DBNull.Value);
            
            // Dates
            cmd.Parameters.AddWithValue("@OffenceDate", (object?)ParseDate(record.TicketDate) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DateEntered", (object?)ParseDate(record.Intake) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DateRetained", (object?)ParseDate(record.IntakeDate) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CourtDate", (object?)ParseDate(record.FirstApp) ?? DBNull.Value);
            
            // Court details
            cmd.Parameters.AddWithValue("@CourtRoom", (object?)record.Rm ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CourtTime", (object?)courtTime ?? DBNull.Value);
            
            // Offence details
            cmd.Parameters.AddWithValue("@SectionNumber", (object?)sectionNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Wording", (object?)offenceTypeId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@OfficerBadge", (object?)officerBadge ?? DBNull.Value);
            
            // Financial information
            cmd.Parameters.AddWithValue("@Fee", ParseDecimal(record.Fee));
            cmd.Parameters.AddWithValue("@FineToPay", ParseDecimal(record.Fine));
            cmd.Parameters.AddWithValue("@Tax", ParseDecimal(record.Tax));
            cmd.Parameters.AddWithValue("@Total", ParseDecimal(record.Total));
            cmd.Parameters.AddWithValue("@TotalPaid", ParseDecimal(record.Paid));
            cmd.Parameters.AddWithValue("@Balance", ParseDecimal(record.Balance));
            
            // Additional info
            cmd.Parameters.AddWithValue("@Guarantee", (object?)record.Guarantee ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Notes", (object?)CombineNotes(record.Notes, record.TicketNotes) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SpecialInstructions", (object?)record.SpecialInstructions ?? DBNull.Value);

            var ticketId = cmd.ExecuteScalar();
            return ticketId != null ? (int)ticketId : 0;
        }

        private void InsertTicketCourtHistory(SqlConnection connection, int ticketId, HTARecord record)
        {
            // Get court IDs
            var courtId = GetCourtLocationId(record.CourtName, record.ICON);
            
            // Parse court time
            TimeSpan? courtTime = null;
            if (!string.IsNullOrWhiteSpace(record.Time))
            {
                if (DateTime.TryParse(record.Time, out DateTime timeResult))
                    courtTime = timeResult.TimeOfDay;
            }

            // Determine if interpreter is needed based on language field
            var interpreterNeeded = !string.IsNullOrWhiteSpace(record.Language) && 
                                   !record.Language.Equals("English", StringComparison.OrdinalIgnoreCase) &&
                                   !record.Language.Equals("EN", StringComparison.OrdinalIgnoreCase);

            var sql = @"
                INSERT INTO [TicketCourtHistory] (
                    TicketId, StoreId, IconId, CourtId,
                    CourtDate, CourtRoom, CourtTime,
                    ClientWantsToAttend, InterpreterNeeded, InterpreterLanguage,
                    Notes, CreatedOnUtc
                )
                VALUES (
                    @TicketId, @StoreId, @IconId, @CourtId,
                    @CourtDate, @CourtRoom, @CourtTime,
                    @ClientWantsToAttend, @InterpreterNeeded, @InterpreterLanguage,
                    @Notes, @CreatedOnUtc
                )";

            using var cmd = new SqlCommand(sql, connection);
            
            cmd.Parameters.AddWithValue("@TicketId", ticketId);
            cmd.Parameters.AddWithValue("@StoreId", _storeId);
            cmd.Parameters.AddWithValue("@IconId", (object?)courtId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CourtId", (object?)courtId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CourtDate", (object?)ParseDate(record.FirstApp) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CourtRoom", (object?)record.Rm ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CourtTime", (object?)courtTime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ClientWantsToAttend", false);
            cmd.Parameters.AddWithValue("@InterpreterNeeded", interpreterNeeded);
            cmd.Parameters.AddWithValue("@InterpreterLanguage", interpreterNeeded ? (object?)record.Language : DBNull.Value);
            cmd.Parameters.AddWithValue("@Notes", DBNull.Value);
            cmd.Parameters.AddWithValue("@CreatedOnUtc", DateTime.UtcNow);

            cmd.ExecuteNonQuery();
        }

        private string? CombineNotes(params string?[] noteParts)
        {
            var validNotes = noteParts.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (validNotes.Count == 0)
                return null;
            
            return string.Join("\n\n", validNotes);
        }

        private static string? GetPrimaryPhone(HTARecord record)
        {
            if (!string.IsNullOrWhiteSpace(record.Cell))
                return record.Cell;
            if (!string.IsNullOrWhiteSpace(record.HomePhone))
                return record.HomePhone;
            if (!string.IsNullOrWhiteSpace(record.BusinessPhone))
                return record.BusinessPhone;
            return null;
        }

        private static DateTime? ParseDate(string? dateString)
        {
            if (string.IsNullOrWhiteSpace(dateString))
                return null;

            string[] formats = {
                "dd-MM-yyyy",
                "d-MM-yyyy",
                "dd-M-yyyy",
                "d-M-yyyy",
                "yyyy-MM-dd",
                "M/d/yyyy",
                "MM/dd/yyyy",
                "dd/MM/yyyy",
                "d/M/yyyy"
            };

            foreach (var format in formats)
            {
                if (DateTime.TryParseExact(dateString, format, CultureInfo.InvariantCulture, 
                    DateTimeStyles.None, out DateTime result))
                {
                    return result;
                }
            }

            if (DateTime.TryParse(dateString, out DateTime parsedDate))
            {
                return parsedDate;
            }

            return null;
        }

        private static decimal ParseDecimal(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            value = value.Replace("$", "").Replace(",", "").Trim();
            
            if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal result))
                return result;
            
            return 0;
        }

        private void ReportUnmappedData(ImportResult result)
        {
            if (_unmappedCourts.Count > 0)
            {
                Console.WriteLine("\n⚠️  UNMAPPED COURTS FOUND");
                Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                Console.WriteLine($"Found {_unmappedCourts.Count} unique court(s) that need to be added to CourtLocation table:\n");
                
                foreach (var court in _unmappedCourts.OrderBy(c => c))
                {
                    Console.WriteLine($"   • {court}");
                    result.Warnings.Add($"Unmapped court: {court}");
                }
                
                Console.WriteLine("\n📝 Action Required:");
                Console.WriteLine("   1. Create CourtJurisdiction records (if needed)");
                Console.WriteLine("   2. Create CourtLocation records with proper IconCode and CourtJurisdictionId");
                Console.WriteLine("   3. Re-run import or update existing tickets with correct court IDs\n");
            }

            if (_unmappedOffences.Count > 0)
            {
                Console.WriteLine("\n⚠️  UNMAPPED OFFENCE TYPES FOUND");
                Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                Console.WriteLine($"Found {_unmappedOffences.Count} unique offence type(s) that need to be added to OffenceType table:\n");
                
                var displayCount = Math.Min(_unmappedOffences.Count, 20);
                foreach (var offence in _unmappedOffences.OrderBy(o => o).Take(displayCount))
                {
                    Console.WriteLine($"   • {offence}");
                    result.Warnings.Add($"Unmapped offence: {offence}");
                }
                
                if (_unmappedOffences.Count > displayCount)
                {
                    Console.WriteLine($"   ... and {_unmappedOffences.Count - displayCount} more");
                }
                
                Console.WriteLine("\n📝 Action Required:");
                Console.WriteLine("   1. Create OffenceType records with proper Name and Statute fields");
                Console.WriteLine("   2. Re-run import or update existing tickets with correct offence IDs\n");
            }

            if (_unmappedCourts.Count == 0 && _unmappedOffences.Count == 0)
            {
                Console.WriteLine("✅ All courts and offence types mapped successfully!\n");
            }
        }
    }

    public class ColumnInfo
    {
        public string ColumnName { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public bool IsNullable { get; set; }
        public string? ColumnDefault { get; set; }
    }
}