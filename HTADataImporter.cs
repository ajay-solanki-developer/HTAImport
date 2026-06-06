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
        private readonly int? _maxTickets;
        private readonly List<string>? _clientIdFilter;
        private readonly bool _importCustomers;
        private readonly bool _importTickets;
        private readonly int _batchSize;
        private HashSet<string>? _ticketFilter = null;
        
        // Lookup caches
        private Dictionary<string, int> _courtLocationCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _offenceTypeCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _stateProvinceCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _dispositionCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _officerCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);  // BadgeNumber -> Id
        private int? _canadaCountryId = null;
        private int? _defaultSourceId = null;
        
        // Track unmapped items for reporting
        private HashSet<string> _unmappedCourts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _unmappedOffences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _unmappedDispositions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public HTADataImporter(string connectionString, int storeId = 1, string firmName = "HTA Import", 
            string? filterCsvPath = null, bool dryRun = true, int? maxTickets = null,
            List<string>? clientIdFilter = null, bool importCustomers = true, bool importTickets = true,
            int batchSize = 100)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _storeId = storeId;
            _firmName = firmName ?? "HTA Import";
            _filterCsvPath = filterCsvPath;
            _dryRun = dryRun;
            _maxTickets = maxTickets;
            _clientIdFilter = clientIdFilter;
            _importCustomers = importCustomers;
            _importTickets = importTickets;
            _batchSize = batchSize > 0 ? batchSize : 100;
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
                Console.WriteLine("📄 Reading data from Garner tables (GarnertblClient + GarnertblTicket + GarnertblTicketType)...");
                var records = ReadDataFromTable();
                Console.WriteLine($"   ✓ Read {records.Count} records from database\n");

                // Step 1.5: Read ALL customers directly from GarnertblClient
                Console.WriteLine("👥 Reading all customers from GarnertblClient...");
                var allCustomers = ReadAllCustomersFromTable();
                Console.WriteLine($"   ✓ Read {allCustomers.Count} unique customers from GarnertblClient\n");

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
                Console.WriteLine($"   ✓ Loaded {_courtLocationCache.Count} courts, {_offenceTypeCache.Count} offence types, {_dispositionCache.Count} dispositions, {_officerCache.Count} officers, {_stateProvinceCache.Count} provinces\n");

                // Step 3: Check database schema
                Console.WriteLine("🔍 Checking database schema...");
                var customerSchema = GetCustomerTableSchema();
                Console.WriteLine($"   ✓ Found {customerSchema.Count} columns in Customer table");
                
                // Step 3.5: Load existing customers from database to avoid duplicates
                Console.WriteLine("\n🔄 Loading existing customers...");
                var customerIds = LoadExistingCustomers(allCustomers);
                Console.WriteLine($"   ✓ Found {customerIds.Count} existing customers in database\n");
                
                // Step 4: Import ALL customers from GarnertblClient
                Console.WriteLine("👥 Importing customers...");
                ImportAllCustomers(allCustomers, customerSchema, result, customerIds);
                result.CustomersImported = customerIds.Count;
                Console.WriteLine($"   ✓ Total {result.CustomersImported} customers available for ticket import\n");

                // Step 5: Update emails with test emails (only for customers without email)
                if (!_dryRun && result.CustomersImported > 0)
                {
                    Console.WriteLine("📧 Generating test emails for imported customers without email...");
                    UpdateCustomerEmails(result);
                    Console.WriteLine($"   ✓ Updated customer emails\n");
                }

                // Step 5.5: Assign Client role to imported customers
                if (!_dryRun && result.CustomersImported > 0)
                {
                    Console.WriteLine("👤 Assigning Client role to imported customers...");
                    AssignClientRole(result);
                    Console.WriteLine();
                }

                // Step 6: Import tickets
                if (_maxTickets.HasValue)
                {
                    Console.WriteLine($"🎫 Importing tickets (limited to {_maxTickets.Value} for testing)...");
                }
                else
                {
                    Console.WriteLine("🎫 Importing tickets...");
                }
                ImportTickets(records, customerIds, result);
                Console.WriteLine($"   ✓ Processed {result.TicketsImported} tickets\n");

                // Step 6.5: Update customer status based on ticket closure
                if (!_dryRun && result.TicketsImported > 0)
                {
                    Console.WriteLine("📊 Updating customer status based on ticket closure...");
                    UpdateCustomerStatus(result);
                    Console.WriteLine($"   ✓ Updated customer statuses\n");
                }

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

                // Build WHERE clause for filters
                var whereClauses = new List<string>();
                
                // Add ticket filter
                if (_ticketFilter != null && _ticketFilter.Count > 0)
                {
                    var quotedTickets = string.Join(",", _ticketFilter.Select(t => $"'{t.Replace("'", "''")}'")); 
                    whereClauses.Add($"T.POT IN ({quotedTickets})");
                }
                
                // Add client ID filter
                if (_clientIdFilter != null && _clientIdFilter.Count > 0)
                {
                    var quotedClientIds = string.Join(",", _clientIdFilter.Select(id => $"'{id.Replace("'", "''")}'")); 
                    whereClauses.Add($"C.pkClientID IN ({quotedClientIds})");
                }
                
                // Combine WHERE clauses
                var whereClause = whereClauses.Count > 0 
                    ? " WHERE " + string.Join(" AND ", whereClauses)
                    : "";

                // Add TOP clause for testing with limited records
                var topClause = _maxTickets.HasValue ? $"TOP {_maxTickets.Value * 2}" : "";  // * 2 to account for potential skips
                
                // Query from Garner original tables with proper joins
                // Using cross-database query: GarnerTempDB tables → LegalShark30May26DB tables
                var sql = $@"SELECT {topClause}
                    -- Original IDs for future mapping
                    T.pkTicketID AS HTATicketId,
                    T.fkClientID AS HTAClientId,
                    C.pkClientID AS ClientPkId,
                    
                    -- Client Information from GarnertblClient
                    CASE 
                        WHEN TRY_CAST(C.IntakeDate AS INT) IS NOT NULL AND TRY_CAST(C.IntakeDate AS INT) > 0 
                        THEN DATEADD(DAY, TRY_CAST(C.IntakeDate AS INT), '1899-12-30')
                        ELSE NULL 
                    END AS IntakeDate,
                    C.First_Name AS FirstName,
                    C.Lastname,
                    C.Address,
                    C.City,
                    C.Prov,
                    C.Postal,
                    C.ynDiscrete,
                    C.homephone,
                    C.businessphone,
                    C.Ext,
                    C.Cell,
                    C.Fax,
                    C.Email,
                    C.fkGenderID AS Gender,
                    C.Notes AS ClientNotes,
                    C.fkLanguageID AS Language,
                    C.kBillingCompanyID AS BillingCompanyId,
                    C.fkSourceID AS SourceId,
                    SRC.[Source] AS SourceName,
                    
                    -- Ticket Information from GarnertblTicket
                    T.POT,
                    T.fkIconID AS IconId,
                    ICON.ICON AS ICON,
                    -- Convert OLE Automation dates (stored as strings) to datetime
                    CASE 
                        WHEN TRY_CAST(T.TicketDate AS INT) IS NOT NULL AND TRY_CAST(T.TicketDate AS INT) > 0 
                        THEN DATEADD(DAY, TRY_CAST(T.TicketDate AS INT), '1899-12-30')
                        ELSE NULL 
                    END AS TicketDate,
                    CASE 
                        WHEN TRY_CAST(T.Intake AS INT) IS NOT NULL AND TRY_CAST(T.Intake AS INT) > 0 
                        THEN DATEADD(DAY, TRY_CAST(T.Intake AS INT), '1899-12-30')
                        ELSE NULL 
                    END AS Intake,
                    T.CustomerTicketNumber,
                    T.fkOffenseSectionID AS SectionNumber,
                    T.fkOffenseWordingID AS OffenseWording,
                    OSEC.SectionNumber AS SectionNumberText,
                    OWRD.Description AS OffenseWordingText,
                    T.SpeedingGoing,
                    T.SpeedingInA,
                    T.fkGuiltyOffenseSectionID AS GuiltyOffenseSectionId,
                    T.fkGuiltyOffenseWordingID AS GuiltyOffenseWordingId,
                    T.GuiltySpeedingGoing,
                    T.GuiltySpeedingInA,
                    OFC.BadgeNumber AS BadgeNumber,
                    T.fkCourtID AS CourtId,
                    ICON.ICON AS CourtIconCode,
                    CASE 
                        WHEN TRY_CAST(T.[1stApp] AS INT) IS NOT NULL AND TRY_CAST(T.[1stApp] AS INT) > 0 
                        THEN DATEADD(DAY, TRY_CAST(T.[1stApp] AS INT), '1899-12-30')
                        ELSE NULL 
                    END AS FirstApp,
                    T.Rm,
                    CASE 
                        WHEN TRY_CAST(T.Time AS FLOAT) IS NOT NULL AND TRY_CAST(T.Time AS FLOAT) > 0 
                        THEN CAST(DATEADD(DAY, TRY_CAST(T.Time AS FLOAT), '1899-12-30') AS TIME)
                        ELSE NULL 
                    END AS Time,
                    T.Disposition AS DispositionId,
                    DISP.Description AS Disposition,
                    T.fkAgentAppearingID AS Name,
                    T.SpecialInstructions,
                    T.Notes AS TicketNotes,
                    CASE 
                        WHEN TRY_CAST(T.DateDisclosureRequested AS INT) IS NOT NULL AND TRY_CAST(T.DateDisclosureRequested AS INT) > 0 
                        THEN DATEADD(DAY, TRY_CAST(T.DateDisclosureRequested AS INT), '1899-12-30')
                        ELSE NULL 
                    END AS DateDisclosureRequested,
                    CASE 
                        WHEN TRY_CAST(T.DateDisclosureReceived AS INT) IS NOT NULL AND TRY_CAST(T.DateDisclosureReceived AS INT) > 0 
                        THEN DATEADD(DAY, TRY_CAST(T.DateDisclosureReceived AS INT), '1899-12-30')
                        ELSE NULL 
                    END AS DateDisclosureReceived,
                    
                    -- Offense Points from GarnertblOffenseSection
                    OS.Points AS OffensePoints,
                    
                    -- Ticket Type from GarnertblTicketType
                    TT.Description AS TicketType,
                    
                    -- Financial Information from GarnertblTicket
                    T.fkGuaranteeID AS Guarantee,
                    T.WePay,
                    T.HePays AS Fine,
                    T.Fee,
                    T.BaseGST AS GST,
                    T.BaseTotal AS Total,
                    T.TotalPayments,
                    T.Balance
                    
                FROM [LegalSharkDB].[dbo].[GarnertblTicket] T
                LEFT JOIN [LegalSharkDB].[dbo].[GarnertblClient] C ON T.fkClientID = C.pkClientID
                LEFT JOIN [LegalSharkDB].[dbo].[GarnertblTicketType] TT ON T.fkTicketTypeID = TT.pkTicketTypeID
                LEFT JOIN [LegalSharkDB].[dbo].[GarnertblOffenseSection] OS ON T.fkOffenseSectionID = OS.pkOffenseSectionID
                LEFT JOIN [LegalSharkDB].[dbo].[GarnertblOffenseSection] OSEC ON T.fkOffenseSectionID = OSEC.pkOffenseSectionID
                LEFT JOIN [LegalSharkDB].[dbo].[GarnertblOffenseWording] OWRD ON T.fkOffenseWordingID = OWRD.pkOffenseWordingID
                LEFT JOIN [LegalSharkDB].[dbo].[GarnertblIcon] ICON ON T.fkIconID = ICON.pkIconID
                LEFT JOIN [LegalSharkDB].[dbo].[GarnertblDisposition] DISP ON T.Disposition = DISP.pkDispositionID
                LEFT JOIN [LegalSharkDB].[dbo].[GarnertblSource] SRC ON C.fkSourceID = SRC.pkSourceID
                LEFT JOIN [LegalSharkDB].[dbo].[GarnertblOfficer] OFC ON T.fkOfficerID = OFC.pkOfficerID{whereClause}
                ORDER BY C.IntakeDate, T.POT";

                using var cmd = new SqlCommand(sql, connection);
                using var reader = cmd.ExecuteReader();
                
                while (reader.Read())
                {
                    var record = new HTARecord
                    {
                        // Original IDs for mapping
                        HTATicketId = GetSafeString(reader, "HTATicketId"),
                        HTAClientId = GetSafeString(reader, "HTAClientId"),
                        
                        // Client Information
                        IntakeDate = GetSafeString(reader, "IntakeDate"),
                        FirstName = GetSafeString(reader, "FirstName"),
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
                        Email = GetSafeString(reader, "Email"),
                        Gender = GetSafeString(reader, "Gender"),
                        Notes = GetSafeString(reader, "ClientNotes"),
                        Language = GetSafeString(reader, "Language"),
                        SourceId = GetSafeString(reader, "SourceId"),
                        SourceName = GetSafeString(reader, "SourceName"),
                        
                        // Ticket Information
                        POT = GetSafeString(reader, "POT"),
                        ICON = GetSafeString(reader, "ICON"),
                        TicketDate = GetSafeString(reader, "TicketDate"),
                        Intake = GetSafeString(reader, "Intake"),
                        CustomerTicketNumber = GetSafeString(reader, "CustomerTicketNumber"),
                        SectionNumber = GetSafeString(reader, "SectionNumber"),
                        OffenseWording = GetSafeString(reader, "OffenseWording"),
                        SectionNumberText = GetSafeString(reader, "SectionNumberText"),
                        OffenseWordingText = GetSafeString(reader, "OffenseWordingText"),
                        SpeedingGoing = GetSafeString(reader, "SpeedingGoing"),
                        SpeedingInA = GetSafeString(reader, "SpeedingInA"),
                        GuiltyOffenseSectionId = GetSafeString(reader, "GuiltyOffenseSectionId"),
                        GuiltyOffenseWordingId = GetSafeString(reader, "GuiltyOffenseWordingId"),
                        GuiltySpeedingGoing = GetSafeString(reader, "GuiltySpeedingGoing"),
                        GuiltySpeedingInA = GetSafeString(reader, "GuiltySpeedingInA"),
                        OffensePoints = GetSafeString(reader, "OffensePoints"),
                        BadgeNumber = GetSafeString(reader, "BadgeNumber"),
                        CourtName = GetSafeString(reader, "CourtId"),
                        CourtIconCode = GetSafeString(reader, "CourtIconCode"),
                        BillingCompanyId = GetSafeString(reader, "BillingCompanyId"),
                        FirstApp = GetSafeString(reader, "FirstApp"),
                        Rm = GetSafeString(reader, "Rm"),
                        Time = GetSafeString(reader, "Time"),
                        Disposition = GetSafeString(reader, "Disposition"),
                        Name = GetSafeString(reader, "Name"),
                        TicketType = GetSafeString(reader, "TicketType"),
                        SpecialInstructions = GetSafeString(reader, "SpecialInstructions"),
                        TicketNotes = GetSafeString(reader, "TicketNotes"),
                        DateDisclosureRequested = GetSafeString(reader, "DateDisclosureRequested"),
                        DateDisclosureReceived = GetSafeString(reader, "DateDisclosureReceived"),
                        
                        // Financial Information
                        Guarantee = GetSafeString(reader, "Guarantee"),
                        WePay = GetSafeString(reader, "WePay"),
                        Fine = GetSafeString(reader, "Fine"),
                        Fee = GetSafeString(reader, "Fee"),
                        Tax = GetSafeString(reader, "GST"),
                        Total = GetSafeString(reader, "Total"),
                        Paid = GetSafeString(reader, "TotalPayments"),
                        Balance = GetSafeString(reader, "Balance")
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

        private List<HTARecord> ReadAllCustomersFromTable()
        {
            var customers = new List<HTARecord>();
            
            try
            {
                using var connection = new SqlConnection(_connectionString);
                connection.Open();

                // Build WHERE clause for client ID filter
                var whereClause = "";
                if (_clientIdFilter != null && _clientIdFilter.Count > 0)
                {
                    var quotedClientIds = string.Join(",", _clientIdFilter.Select(id => $"'{id.Replace("'", "''")}'")); 
                    whereClause = $" WHERE C.pkClientID IN ({quotedClientIds})";
                }

                // Query ALL customers from GarnertblClient directly
                var sql = $@"SELECT 
                    -- Original IDs for mapping
                    C.pkClientID AS HTAClientId,
                    C.pkClientID AS ClientPkId,
                    
                    -- Client Information
                    CASE 
                        WHEN TRY_CAST(C.IntakeDate AS INT) IS NOT NULL AND TRY_CAST(C.IntakeDate AS INT) > 0 
                        THEN DATEADD(DAY, TRY_CAST(C.IntakeDate AS INT), '1899-12-30')
                        ELSE NULL 
                    END AS IntakeDate,
                    C.First_Name AS FirstName,
                    C.Lastname,
                    C.Address,
                    C.City,
                    C.Prov,
                    C.Postal,
                    C.ynDiscrete,
                    C.homephone,
                    C.businessphone,
                    C.Ext,
                    C.Cell,
                    C.Fax,
                    C.Email,
                    C.fkGenderID AS Gender,
                    C.Notes AS ClientNotes,
                    C.fkLanguageID AS Language,
                    C.kBillingCompanyID AS BillingCompanyId,
                    C.fkSourceID AS SourceId,
                    SRC.[Source] AS SourceName
                    
                FROM [LegalSharkDB].[dbo].[GarnertblClient] C
                LEFT JOIN [LegalSharkDB].[dbo].[GarnertblSource] SRC ON C.fkSourceID = SRC.pkSourceID{whereClause}
                ORDER BY C.pkClientID";

                using var cmd = new SqlCommand(sql, connection);
                using var reader = cmd.ExecuteReader();
                
                while (reader.Read())
                {
                    var customer = new HTARecord
                    {
                        // Original IDs for mapping
                        HTAClientId = GetSafeString(reader, "HTAClientId"),
                        
                        // Client Information
                        IntakeDate = GetSafeString(reader, "IntakeDate"),
                        FirstName = GetSafeString(reader, "FirstName"),
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
                        Email = GetSafeString(reader, "Email"),
                        Gender = GetSafeString(reader, "Gender"),
                        Notes = GetSafeString(reader, "ClientNotes"),
                        Language = GetSafeString(reader, "Language"),
                        SourceId = GetSafeString(reader, "SourceId"),
                        SourceName = GetSafeString(reader, "SourceName"),
                        BillingCompanyId = GetSafeString(reader, "BillingCompanyId")
                    };
                    
                    customers.Add(customer);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ✗ Failed to read customers: {ex.Message}");
                throw;
            }

            return customers;
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
                
                // Handle TimeSpan columns (TIME datatype from SQL)
                if (value is TimeSpan ts)
                    return ts.ToString(@"hh\:mm\:ss");
                
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
                _canadaCountryId = countryResult != null ? Convert.ToInt32(countryResult) : (int?)null;

                // Load State/Provinces for Canada
                if (_canadaCountryId.HasValue)
                {
                    var stateCmd = new SqlCommand($"SELECT Id, Name, Abbreviation FROM StateProvince WHERE CountryId = {_canadaCountryId.Value}", connection);
                    using (var stateReader = stateCmd.ExecuteReader())
                    {
                        while (stateReader.Read())
                        {
                            var id = Convert.ToInt32(stateReader.GetValue(0));
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
                        var id = Convert.ToInt32(courtReader.GetValue(0));
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
                        var id = Convert.ToInt32(offenceReader.GetValue(0));
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

                // Load Dispositions - use ID (primary key) not pkDispositionID (source ID)
                var dispositionCmd = new SqlCommand("SELECT ID, Description FROM Disposition", connection);
                using (var dispositionReader = dispositionCmd.ExecuteReader())
                {
                    while (dispositionReader.Read())
                    {
                        var id = Convert.ToInt32(dispositionReader.GetValue(0));
                        var name = dispositionReader.GetString(1);
                        _dispositionCache[name] = id;
                    }
                }

                // Load Officers from the new Officer table (filtered by StoreId)
                var officerCmd = new SqlCommand($"SELECT Id, BadgeNumber FROM Officer WHERE StoreId = {_storeId} AND BadgeNumber IS NOT NULL AND IsActive = 1", connection);
                using (var officerReader = officerCmd.ExecuteReader())
                {
                    while (officerReader.Read())
                    {
                        var id = Convert.ToInt32(officerReader.GetValue(0));
                        
                        // Check for NULL before reading BadgeNumber
                        if (!officerReader.IsDBNull(1))
                        {
                            var badgeNumber = officerReader.GetString(1);
                            if (!string.IsNullOrWhiteSpace(badgeNumber))
                            {
                                _officerCache[badgeNumber.Trim()] = id;
                            }
                        }
                    }
                }

                // Load or create default Source
                var sourceCmd = new SqlCommand($"SELECT TOP 1 Id FROM Source WHERE StoreId = {_storeId} AND Name = 'Data Import' AND IsActive = 1", connection);
                var sourceResult = sourceCmd.ExecuteScalar();
                if (sourceResult != null)
                {
                    _defaultSourceId = Convert.ToInt32(sourceResult);
                }
                else if (!_dryRun)
                {
                    // Create default source
                    var createSourceCmd = new SqlCommand(
                        $"INSERT INTO Source (StoreId, Name, Description, IsActive, CreatedOnUtc) " +
                        $"OUTPUT INSERTED.Id " +
                        $"VALUES ({_storeId}, 'Data Import', 'Records imported from legacy system', 1, GETUTCDATE())", 
                        connection);
                    _defaultSourceId = Convert.ToInt32(createSourceCmd.ExecuteScalar());
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
            
            // Only track as unmapped if there's actually data that couldn't be mapped
            // Skip tracking if both fields are empty (ticket with no offence data)
            if (!string.IsNullOrWhiteSpace(sectionNumber) || !string.IsNullOrWhiteSpace(offenceDescription))
            {
                var offenceDesc = $"Section: '{sectionNumber ?? "(none)"}', Description: '{offenceDescription ?? "(none)"}'";
                _unmappedOffences.Add(offenceDesc);
            }
            
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

        private int? GetDispositionId(string? dispositionName)
        {
            if (string.IsNullOrWhiteSpace(dispositionName))
                return null;
            
            // Try exact match
            if (_dispositionCache.TryGetValue(dispositionName, out int id))
                return id;
            
            // Try partial match (for cases like "Guilty  Plea" vs "Guilty Plea")
            var normalized = dispositionName.Trim();
            var match = _dispositionCache.FirstOrDefault(kvp => 
                kvp.Key.Replace(" ", "").Equals(normalized.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            
            if (!match.Equals(default(KeyValuePair<string, int>)))
                return match.Value;
            
            // Track unmapped disposition for reporting
            _unmappedDispositions.Add(dispositionName);
            
            return null;
        }

        private int? GetOrCreateOfficerId(SqlConnection connection, string? badgeNumber, string? firstName, string? lastName)
        {
            if (string.IsNullOrWhiteSpace(badgeNumber))
                return null;
            
            var badge = badgeNumber.Trim();
            
            // Check cache first
            if (_officerCache.TryGetValue(badge, out int officerId))
                return officerId;
            
            // Query GarnertblOfficer source table to get FirstName, LastName, and DivisionNumber for this badge
            string? officerFirstName = null;
            string? officerLastName = null;
            string? officerDivision = null;
            
            var sourceQuery = new SqlCommand("SELECT FirstName, LastName, DivisionNumber FROM [LegalSharkDB].[dbo].[GarnertblOfficer] WHERE BadgeNumber = @BadgeNumber", connection);
            sourceQuery.Parameters.AddWithValue("@BadgeNumber", badge);
            using (var reader = sourceQuery.ExecuteReader())
            {
                if (reader.Read())
                {
                    officerFirstName = reader.IsDBNull(0) ? null : reader.GetString(0);
                    officerLastName = reader.IsDBNull(1) ? null : reader.GetString(1);
                    officerDivision = reader.IsDBNull(2) ? null : reader.GetString(2);
                }
            }
            
            // Combine FirstName and LastName for the new Officer table
            var combinedName = string.Join(" ", new[] { officerFirstName?.Trim(), officerLastName?.Trim() }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(combinedName))
                combinedName = $"Officer {badge}"; // Fallback if no name found
            
            // Officer doesn't exist, create it (only if not dry run)
            if (_dryRun)
            {
                Console.WriteLine($"   [DRY RUN] Would create officer: {combinedName} (Badge: {badge})");
                return null;
            }
            
            try
            {
                var sql = @"
                    INSERT INTO [Officer] (
                        StoreId, FirstName, DivisionNumber, BadgeNumber, IsRetired, IsActive, CreatedOnUtc, UpdatedOnUtc
                    )
                    OUTPUT INSERTED.Id
                    VALUES (
                        @StoreId, @FirstName, @DivisionNumber, @BadgeNumber, 0, 1, @CreatedOnUtc, @UpdatedOnUtc
                    )";
                
                using var cmd = new SqlCommand(sql, connection);
                var now = DateTime.UtcNow;
                cmd.Parameters.AddWithValue("@FirstName", combinedName);
                cmd.Parameters.AddWithValue("@DivisionNumber", (object?)officerDivision ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@StoreId", _storeId);
                cmd.Parameters.AddWithValue("@BadgeNumber", badge);
                cmd.Parameters.AddWithValue("@CreatedOnUtc", now);
                cmd.Parameters.AddWithValue("@UpdatedOnUtc", now);
                
                var newId = Convert.ToInt32(cmd.ExecuteScalar());
                
                // Add to cache for future lookups
                _officerCache[badge] = newId;
                
                return newId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠ Warning: Failed to create officer {badge}: {ex.Message}");
                return null;
            }
        }

        private int? GetOrCreateSourceId(SqlConnection connection, string sourceName)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
                return _defaultSourceId;
            
            try
            {
                // Check if source exists for this store
                var checkSql = "SELECT TOP 1 Id FROM Source WHERE StoreId = @StoreId AND Name = @Name AND IsActive = 1";
                using (var checkCmd = new SqlCommand(checkSql, connection))
                {
                    checkCmd.Parameters.AddWithValue("@StoreId", _storeId);
                    checkCmd.Parameters.AddWithValue("@Name", sourceName);
                    var result = checkCmd.ExecuteScalar();
                    if (result != null)
                        return Convert.ToInt32(result);
                }
                
                // Source not found, return default
                return _defaultSourceId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠ Warning: Failed to lookup source '{sourceName}': {ex.Message}");
                return _defaultSourceId;
            }
        }

        private int? GetBillingCompanyId(SqlConnection connection, string billingCompanySourceId)
        {
            if (string.IsNullOrWhiteSpace(billingCompanySourceId))
                return null;
            
            try
            {
                // First, get the CompanyName from Garner database using the source ID
                string? companyName = null;
                var garnerSql = "SELECT TOP 1 CompanyName FROM [LegalSharkDB].[dbo].[GarnertblBillingCompany] WHERE pkBillingCompanyID = @SourceId";
                using (var garnerCmd = new SqlCommand(garnerSql, connection))
                {
                    garnerCmd.Parameters.AddWithValue("@SourceId", billingCompanySourceId);
                    var result = garnerCmd.ExecuteScalar();
                    if (result != null && result != DBNull.Value)
                        companyName = result.ToString()?.Trim();
                }
                
                // If we found the company name, look it up in the destination BillingCompany table
                if (!string.IsNullOrWhiteSpace(companyName))
                {
                    var lookupSql = "SELECT TOP 1 Id FROM BillingCompany WHERE StoreId = @StoreId AND LTRIM(RTRIM(CompanyName)) = LTRIM(RTRIM(@CompanyName)) AND IsActive = 1";
                    using (var lookupCmd = new SqlCommand(lookupSql, connection))
                    {
                        lookupCmd.Parameters.AddWithValue("@StoreId", _storeId);
                        lookupCmd.Parameters.AddWithValue("@CompanyName", companyName);
                        var lookupResult = lookupCmd.ExecuteScalar();
                        if (lookupResult != null)
                            return Convert.ToInt32(lookupResult);
                    }
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠ Warning: Failed to lookup billing company '{billingCompanySourceId}': {ex.Message}");
                return null;
            }
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

        private Dictionary<string, int> LoadExistingCustomers(List<HTARecord> customers)
        {
            // Map HTAClientId (pkClientID) -> Customer.Id
            var customerIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            using var connection = new SqlConnection(_connectionString);
            connection.Open();

            try
            {
                // Load customers from Customer table using HTAClientId
                var sql = @"
                    SELECT 
                        Id, 
                        HTAClientId
                    FROM Customer 
                    WHERE RegisteredInStoreId = @StoreId 
                        AND Deleted = 0 
                        AND HTAClientId IS NOT NULL";

                using var cmd = new SqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@StoreId", _storeId);

                using var reader = cmd.ExecuteReader();
                int loadedCount = 0;
                while (reader.Read())
                {
                    var customerId = reader.GetInt32(0);
                    var htaClientId = reader.GetString(1).Trim();

                    // Use HTAClientId (pkClientID) as the mapping key
                    if (!string.IsNullOrWhiteSpace(htaClientId) && !customerIds.ContainsKey(htaClientId))
                    {
                        customerIds[htaClientId] = customerId;
                        loadedCount++;
                    }
                }
                
                Console.WriteLine($"   Loaded {customerIds.Count} customers mapped by HTAClientId (pkClientID)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠ Error loading customers: {ex.Message}");
            }

            return customerIds;
        }

        private void ImportAllCustomers(List<HTARecord> customers, List<ColumnInfo> schema, ImportResult result, Dictionary<string, int> customerIds)
        {
            // Import ALL customers from GarnertblClient (no grouping, use pkClientID as unique key)
            using var connection = new SqlConnection(_connectionString);
            if (!_dryRun)
                connection.Open();

            int count = 0;
            int newCustomerCount = 0;
            int skippedCount = 0;
            
            foreach (var customer in customers)
            {
                count++;
                
                // Skip customers without HTAClientId (pkClientID is required)
                if (string.IsNullOrWhiteSpace(customer.HTAClientId))
                {
                    result.Warnings.Add($"Skipping customer without pkClientID: {customer.FirstName} {customer.LastName}");
                    skippedCount++;
                    continue;
                }
                
                // Use HTAClientId (pkClientID) as the unique key
                var customerKey = customer.HTAClientId.Trim();
                
                // Skip if customer already exists in database
                if (customerIds.ContainsKey(customerKey))
                {
                    skippedCount++;
                    continue;
                }

                try
                {
                    if (_dryRun)
                    {
                        customerIds[customerKey] = count;
                        newCustomerCount++;
                        if (newCustomerCount <= 5)
                        {
                            Console.WriteLine($"   [DRY RUN] Would import: {customer.FirstName} {customer.LastName} - {customer.City} (pkClientID: {customerKey})");
                        }
                        else if (newCustomerCount == 6)
                        {
                            Console.WriteLine($"   [DRY RUN] ... and {customers.Count - 5 - skippedCount} more customers");
                        }
                    }
                    else
                    {
                        var customerId = InsertCustomer(connection, customer, schema);
                        customerIds[customerKey] = customerId;
                        newCustomerCount++;
                        
                        if (newCustomerCount % 100 == 0)
                        {
                            Console.WriteLine($"   Imported {newCustomerCount} new customers...");
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Failed to import customer {customer.FirstName} {customer.LastName} (pkClientID: {customerKey}): {ex.Message}");
                }
            }
            
            if (newCustomerCount > 0)
            {
                Console.WriteLine($"   ✓ Imported {newCustomerCount} new customers");
            }
            
            if (skippedCount > 0)
            {
                Console.WriteLine($"   ℹ Skipped {skippedCount} customers (already exist or missing pkClientID)");
            }
        }

        private void ImportCustomers(List<HTARecord> records, List<ColumnInfo> schema, ImportResult result, Dictionary<string, int> customerIds)
        {
            // Group by HTAClientId to get unique customers (HTAClientId is the fkClientID from Garner)
            var uniqueCustomers = records
                .Where(r => !string.IsNullOrWhiteSpace(r.HTAClientId))
                .GroupBy(r => r.HTAClientId.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            using var connection = new SqlConnection(_connectionString);
            if (!_dryRun)
                connection.Open();

            int count = 0;
            int newCustomerCount = 0;
            foreach (var record in uniqueCustomers)
            {
                count++;
                
                // Skip customers without HTAClientId (needed for ticket matching)
                if (string.IsNullOrWhiteSpace(record.HTAClientId))
                {
                    result.Warnings.Add($"Skipping customer without HTAClientId: {record.FirstName} {record.LastName}");
                    continue;
                }
                
                // Use HTAClientId as the key for matching with tickets
                var customerKey = record.HTAClientId.Trim();
                
                // Skip if customer already exists in database
                if (customerIds.ContainsKey(customerKey))
                {
                    continue;
                }

                try
                {
                    if (_dryRun)
                    {
                        customerIds[customerKey] = count;
                        newCustomerCount++;
                        if (newCustomerCount <= 5)
                        {
                            Console.WriteLine($"   [DRY RUN] Would import: {record.FirstName} {record.LastName} - {record.City} (HTAClientId: {customerKey})");
                        }
                        else if (newCustomerCount == 6)
                        {
                            Console.WriteLine($"   [DRY RUN] ... and {uniqueCustomers.Count - 5} more customers");
                        }
                    }
                    else
                    {
                        var customerId = InsertCustomer(connection, record, schema);
                        customerIds[customerKey] = customerId;
                        newCustomerCount++;
                        
                        if (newCustomerCount % 100 == 0)
                        {
                            Console.WriteLine($"   Imported {newCustomerCount} new customers...");
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Failed to import customer {record.FirstName} {record.LastName}: {ex.Message}");
                }
            }
            
            if (newCustomerCount > 0)
            {
                Console.WriteLine($"   ✓ Imported {newCustomerCount} new customers");
            }
        }

        private int InsertCustomer(SqlConnection connection, HTARecord record, List<ColumnInfo> schema)
        {
            var columns = new List<string>();
            var parameters = new List<string>();
            var values = new Dictionary<string, object?>();

            // Use email from source if available, otherwise generate temporary email
            var email = string.IsNullOrWhiteSpace(record.Email) 
                ? $"temp-{Guid.NewGuid().ToString().Substring(0, 8)}@test.com"
                : record.Email.Trim();

            // Get foreign key IDs
            var stateProvinceId = GetStateProvinceId(record.Prov) ?? 0;
            var countryId = _canadaCountryId ?? 0;
            
            // Map source from Garner data, fallback to default source if not found
            var sourceId = _defaultSourceId;
            if (!string.IsNullOrWhiteSpace(record.SourceName))
            {
                sourceId = GetOrCreateSourceId(connection, record.SourceName);
            }
            
            // Map BillingCompanyId
            int? billingCompanyId = null;
            if (!string.IsNullOrWhiteSpace(record.BillingCompanyId))
            {
                billingCompanyId = GetBillingCompanyId(connection, record.BillingCompanyId);
            }

            // Map our data to columns
            var dataMap = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["CustomerGuid"] = Guid.NewGuid(),
                ["FirstName"] = record.FirstName,
                ["LastName"] = record.LastName,
                ["Email"] = email,  // Use email from source or temporary email
                ["Gender"] = record.Gender,
                ["StreetAddress"] = record.Address,
                ["City"] = record.City,
                ["County"] = record.Prov,
                ["ZipPostalCode"] = record.Postal,
                ["Phone"] = GetPrimaryPhone(record),
                ["HomePhone"] = record.HomePhone,
                ["BusinessPhone"] = record.BusinessPhone,
                ["CellPhone"] = record.Cell,
                ["Fax"] = record.Fax,
                ["BillingCompanyId"] = billingCompanyId,
                ["ContactTypeId"] = 1,
                ["EntityTypeId"] = 1,
                ["RegisteredInStoreId"] = _storeId,
                ["CountryId"] = countryId,
                ["StateProvinceId"] = stateProvinceId,
                ["SourceId"] = sourceId,
                ["AdminComment"] = record.Notes,
                ["CreatedOnUtc"] = ParseDate(record.IntakeDate) ?? DateTime.UtcNow,
                ["LastActivityDateUtc"] = DateTime.UtcNow,
                ["Active"] = true,
                ["Deleted"] = false,
                ["IsSystemAccount"] = false,
                ["HstExempt"] = false,
                ["ImportedFromHTA"] = true,
                ["ImportedFromFirm"] = _firmName,
                ["HTAClientId"] = record.HTAClientId  // Original Garner Client ID for mapping
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

            return Convert.ToInt32(cmd.ExecuteScalar()!);
        }

        private void AssignClientRole(ImportResult result)
        {
            try
            {
                // Use RoleId 6 for Client role
                var clientRoleId = 6;

                using var connection = new SqlConnection(_connectionString);
                connection.Open();

                // Assign Client role to all newly imported customers
                var sql = $@"
                    INSERT INTO [Customer_CustomerRole_Mapping] (Customer_Id, CustomerRole_Id)
                    SELECT c.Id, @RoleId
                    FROM [Customer] c
                    LEFT JOIN [Customer_CustomerRole_Mapping] m ON c.Id = m.Customer_Id AND m.CustomerRole_Id = @RoleId
                    WHERE c.ImportedFromHTA = 1
                    AND c.RegisteredInStoreId = @StoreId
                    AND m.Customer_Id IS NULL";

                using var cmd = new SqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@RoleId", clientRoleId);
                cmd.Parameters.AddWithValue("@StoreId", _storeId);
                var assigned = cmd.ExecuteNonQuery();
                
                Console.WriteLine($"   ✓ Assigned Client role (RoleId: {clientRoleId}) to {assigned} customers");
                
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

        private void UpdateCustomerStatus(ImportResult result)
        {
            try
            {
                using var connection = new SqlConnection(_connectionString);
                connection.Open();

                // Update customer Active status based on ticket closure
                // If all tickets are closed (IsDone = 1), set Customer.Active = 0 (Closed)
                // If any ticket is open (IsDone = 0), set Customer.Active = 1 (Active)
                var sql = @"
                    UPDATE c
                    SET c.Active = CASE 
                        WHEN EXISTS (
                            SELECT 1 
                            FROM [Ticket] t 
                            WHERE t.CustomerId = c.Id 
                            AND t.IsDone = 0
                        ) THEN 1  -- Active if any open tickets
                        ELSE 0    -- Closed if all tickets are closed
                    END
                    FROM [Customer] c
                    WHERE c.ImportedFromHTA = 1
                    AND c.RegisteredInStoreId = @StoreId
                    AND EXISTS (SELECT 1 FROM [Ticket] WHERE CustomerId = c.Id)";

                using var cmd = new SqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@StoreId", _storeId);
                var updated = cmd.ExecuteNonQuery();
                
                Console.WriteLine($"   ✓ Updated status for {updated} customers");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to update customer status: {ex.Message}");
                Console.WriteLine($"   ⚠ Warning: {ex.Message}");
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
            
            // Sort records by CustomerTicketNumber (entry order) then IntakeDate
            var sortedRecords = records
                .OrderBy(r => ParseInt(r.CustomerTicketNumber) ?? int.MaxValue)
                .ThenBy(r => ParseDate(r.IntakeDate) ?? DateTime.MaxValue)
                .ToList();
            
            // Track file numbers per Year-Month
            var fileNumberCounters = new Dictionary<string, int>();

            foreach (var record in sortedRecords)
            {
                // Check if we've reached the ticket limit
                if (_maxTickets.HasValue && count >= _maxTickets.Value)
                {
                    Console.WriteLine($"   ⚠ Reached ticket limit of {_maxTickets.Value}. Stopping import.");
                    break;
                }
                
                // Use HTAClientId for matching instead of name/address
                if (string.IsNullOrWhiteSpace(record.HTAClientId))
                {
                    skipped++;
                    continue;
                }
                
                if (!customerIds.TryGetValue(record.HTAClientId.Trim(), out int customerId))
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
                            try
                            {
                                InsertTicketCourtHistory(connection, ticketId, record);
                            }
                            catch (Exception)
                            {
                                // Silently skip if table doesn't exist - log only once
                                if (count == 1)
                                {
                                    result.Warnings.Add("TicketCourtHistory table not found - history tracking disabled. Run History_Tables_Setup.sql to enable.");
                                }
                            }
                        }
                        
                        count++;
                        
                        // Show progress more frequently for testing
                        if (_maxTickets.HasValue && _maxTickets.Value <= 20)
                        {
                            // Show each ticket when testing with small limits
                            Console.WriteLine($"   ✓ Imported ticket {count}/{_maxTickets.Value}: {record.POT} (File: {fileNumber})");
                        }
                        else if (count % 10 == 0)
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
                
                // Debug: Check if missing HTAClientIds exist in other stores
                if (!_dryRun && skipped > 0)
                {
                    try
                    {
                        Console.WriteLine($"\n   Checking if any skipped HTAClientIds exist in other stores...");
                        var checkSql = @"
                            SELECT DISTINCT TOP 10
                                c.HTAClientId, 
                                c.RegisteredInStoreId, 
                                c.FirstName, 
                                c.LastName,
                                COUNT(*) OVER (PARTITION BY c.HTAClientId) as OccurrenceCount
                            FROM Customer c
                            WHERE c.HTAClientId IN (
                                SELECT DISTINCT TOP 10 T.fkClientID
                                FROM [LegalSharkDB].[dbo].[GarnertblTicket] T
                                WHERE T.fkClientID NOT IN (
                                    SELECT HTAClientId 
                                    FROM Customer 
                                    WHERE RegisteredInStoreId = @StoreId 
                                    AND HTAClientId IS NOT NULL
                                    AND Deleted = 0
                                )
                            )
                            AND c.Deleted = 0
                            ORDER BY c.HTAClientId";
                        
                        using var checkCmd = new SqlCommand(checkSql, connection);
                        checkCmd.Parameters.AddWithValue("@StoreId", _storeId);
                        using var checkReader = checkCmd.ExecuteReader();
                        
                        int foundInOtherStores = 0;
                        while (checkReader.Read())
                        {
                            var htaId = checkReader.GetString(0);
                            var storeId = checkReader.GetInt32(1);
                            var fname = checkReader.GetString(2);
                            var lname = checkReader.GetString(3);
                            Console.WriteLine($"   → HTAClientId '{htaId}' found in StoreId={storeId} ({fname} {lname})");
                            foundInOtherStores++;
                        }
                        
                        if (foundInOtherStores == 0)
                        {
                            Console.WriteLine($"   → No missing HTAClientIds found in other stores (they may not be imported yet)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   ⚠ Could not check other stores: {ex.Message}");
                    }
                }
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
            // Get foreign key IDs from master data (filtered by StoreId)
            var courtId = GetCourtLocationId(record.CourtName, record.CourtIconCode);
            // Use actual section text and description (not FK IDs) to match OffenceType
            var offenceTypeId = GetOffenceTypeId(record.SectionNumberText, record.OffenseWordingText);
            var guiltyOffenceTypeId = GetOffenceTypeId(record.GuiltyOffenseSectionId, record.GuiltyOffenseWordingId);
            var dispositionId = GetDispositionId(record.Disposition);
            var officerId = GetOrCreateOfficerId(connection, record.BadgeNumber, record.FirstName, record.LastName);
            
            // Ticket with disposition should be marked as closed (IsDone = 1)
            var isClosed = dispositionId.HasValue ? 1 : 0;
            
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

            // Calculate Disclosure Status based on dates
            int? disclosureStatus = CalculateDisclosureStatus(
                record.DateDisclosureRequested, 
                record.DateDisclosureReceived
            );

            // Parse PTS (demerit points)
            int? ptsOffence = null;
            if (!string.IsNullOrWhiteSpace(record.OffensePoints) && int.TryParse(record.OffensePoints, out int pts))
                ptsOffence = pts;

            // Build GuiltySpeedInfo from guilty speeding fields
            string? guiltySpeedInfo = null;
            if (!string.IsNullOrWhiteSpace(record.GuiltySpeedingGoing) || !string.IsNullOrWhiteSpace(record.GuiltySpeedingInA))
            {
                guiltySpeedInfo = $"{record.GuiltySpeedingGoing ?? "0"} in a {record.GuiltySpeedingInA ?? "0"}";
            }

            var sql = @"
                INSERT INTO [Ticket] (
                    CustomerId, IconId, CourtId, 
                    TicketNumber, FileNumber, OffenceNumber,
                    OffenceDate, DateEntered, DateRetained,
                    CourtDate, CourtRoom, CourtTime,
                    SectionNumber, Wording, OfficerId, OfficerBadge,
                    GuiltySectionNumber, GuiltyWording, GuiltySpeedInfo, PtsOffence,
                    Fee, FineToPay, Tax, Total, TotalPaid, Balance,
                    Guarantee, Disposition, Notes, SpecialInstructions,
                    DateOfRequest, DateReceived, DisclosureStatus,
                    StatusKey, IsImported,
                    Deleted, ClientWantsToAttend, InterpreterNeeded, IsAccident,
                    IsPreTrialNeeded, IsQueued, IsDone,
                    HTATicketId, HTAClientId
                )
                OUTPUT INSERTED.Id
                VALUES (
                    @CustomerId, @IconId, @CourtId,
                    @TicketNumber, @FileNumber, @OffenceNumber,
                    @OffenceDate, @DateEntered, @DateRetained,
                    @CourtDate, @CourtRoom, @CourtTime,
                    @SectionNumber, @Wording, @OfficerId, @OfficerBadge,
                    @GuiltySectionNumber, @GuiltyWording, @GuiltySpeedInfo, @PtsOffence,
                    @Fee, @FineToPay, @Tax, @Total, @TotalPaid, @Balance,
                    @Guarantee, @Disposition, @Notes, @SpecialInstructions,
                    @DateOfRequest, @DateReceived, @DisclosureStatus,
                    'Imported', 1,
                    0, 0, 0, 0,
                    0, 0, @IsClosed,
                    @HTATicketId, @HTAClientId
                )";

            using var cmd = new SqlCommand(sql, connection);
            
            // Customer and Court info
            cmd.Parameters.AddWithValue("@CustomerId", customerId);
            cmd.Parameters.AddWithValue("@IconId", (object?)courtId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CourtId", (object?)courtId ?? DBNull.Value);
            
            // Ticket identification - TicketNumber and OffenceNumber are both POT
            cmd.Parameters.AddWithValue("@TicketNumber", (object?)record.POT ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FileNumber", fileNumber);
            cmd.Parameters.AddWithValue("@OffenceNumber", (object?)record.POT ?? DBNull.Value);  // POT is the offence number
            
            // Dates - Use CourtDate if available, otherwise use FirstApp
            cmd.Parameters.AddWithValue("@OffenceDate", (object?)ParseDate(record.TicketDate) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DateEntered", (object?)ParseDate(record.Intake) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DateRetained", (object?)ParseDate(record.IntakeDate) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CourtDate", (object?)ParseDate(record.FirstApp) ?? DBNull.Value);
            
            // Court details
            cmd.Parameters.AddWithValue("@CourtRoom", (object?)record.Rm ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CourtTime", (object?)courtTime ?? DBNull.Value);
            
            // Offence details - All four fields link to OffenceType table by ID (filtered by StoreId)
            cmd.Parameters.AddWithValue("@SectionNumber", (object?)offenceTypeId ?? DBNull.Value);  // Links to OffenceType table
            cmd.Parameters.AddWithValue("@Wording", (object?)offenceTypeId ?? DBNull.Value);  // Links to OffenceType table
            cmd.Parameters.AddWithValue("@OfficerId", (object?)officerId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@OfficerBadge", (object?)officerBadge ?? DBNull.Value);
            
            // Guilty offence details - Both fields link to OffenceType table by ID (filtered by StoreId)
            cmd.Parameters.AddWithValue("@GuiltySectionNumber", (object?)guiltyOffenceTypeId ?? DBNull.Value);  // Links to OffenceType table
            cmd.Parameters.AddWithValue("@GuiltyWording", (object?)guiltyOffenceTypeId ?? DBNull.Value);  // Links to OffenceType table
            cmd.Parameters.AddWithValue("@GuiltySpeedInfo", (object?)guiltySpeedInfo ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@PtsOffence", (object?)ptsOffence ?? DBNull.Value);
            
            // Financial information - Fee, Tax (GST), Total
            var feeAmount = ParseDecimal(record.Fee);
            var taxAmount = ParseDecimal(record.Tax);  // GST from CSV
            var totalAmount = ParseDecimal(record.Total);
            
            cmd.Parameters.AddWithValue("@Fee", feeAmount);
            cmd.Parameters.AddWithValue("@FineToPay", ParseDecimal(record.Fine));  // What client owes to court (HePays)
            cmd.Parameters.AddWithValue("@Tax", taxAmount);  // GST/Tax
            cmd.Parameters.AddWithValue("@Total", totalAmount);  // Total amount
            cmd.Parameters.AddWithValue("@TotalPaid", ParseDecimal(record.Paid));  // Amount paid
            cmd.Parameters.AddWithValue("@Balance", ParseDecimal(record.Balance));  // Balance remaining
            
            // Disposition info
            cmd.Parameters.AddWithValue("@Guarantee", (object?)record.Guarantee ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Disposition", (object?)dispositionId ?? DBNull.Value);  // Links to Disposition table
            cmd.Parameters.AddWithValue("@Notes", (object?)CombineNotes(record.Notes, record.TicketNotes) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SpecialInstructions", (object?)record.SpecialInstructions ?? DBNull.Value);
            
            // Disclosure information
            cmd.Parameters.AddWithValue("@DateOfRequest", (object?)ParseDate(record.DateDisclosureRequested) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DateReceived", (object?)ParseDate(record.DateDisclosureReceived) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DisclosureStatus", (object?)disclosureStatus ?? DBNull.Value);
            
            // Ticket status - mark as closed if has disposition
            cmd.Parameters.AddWithValue("@IsClosed", isClosed);
            
            // Original Garner IDs for reverse mapping
            cmd.Parameters.AddWithValue("@HTATicketId", (object?)record.HTATicketId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@HTAClientId", (object?)record.HTAClientId ?? DBNull.Value);

            var ticketId = cmd.ExecuteScalar();
            return ticketId != null ? Convert.ToInt32(ticketId) : 0;
        }

        private void InsertTicketCourtHistory(SqlConnection connection, int ticketId, HTARecord record)
        {
            // Get court IDs
            var courtId = GetCourtLocationId(record.CourtName, record.CourtIconCode);
            
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
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-dd",
                "dd-MM-yyyy",
                "d-MM-yyyy",
                "dd-M-yyyy",
                "d-M-yyyy",
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

        private static int? ParseInt(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim();
            
            if (int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out int result))
                return result;
            
            return null;
        }

        private int? CalculateDisclosureStatus(string? dateRequested, string? dateReceived)
        {
            // Disclosure Status: 1=Requested, 2=Received, 3=Pending, 4=Not Requested
            
            var requested = ParseDate(dateRequested);
            var received = ParseDate(dateReceived);
            
            if (received.HasValue)
            {
                // If we have a received date, status is "Received"
                return 2;
            }
            else if (requested.HasValue)
            {
                // If requested but not received, status is "Pending" (or "Requested")
                // Using 3 for Pending as it's more accurate for "requested but not yet received"
                return 3;
            }
            else
            {
                // No dates means "Not Requested"
                return 4;
            }
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

            if (_unmappedDispositions.Count > 0)
            {
                Console.WriteLine("\n⚠️  UNMAPPED DISPOSITIONS FOUND");
                Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                Console.WriteLine($"Found {_unmappedDispositions.Count} unique disposition(s) that need to be added to Disposition table:\n");
                
                foreach (var disposition in _unmappedDispositions.OrderBy(d => d))
                {
                    Console.WriteLine($"   • {disposition}");
                    result.Warnings.Add($"Unmapped disposition: {disposition}");
                }
                
                Console.WriteLine("\n📝 Action Required:");
                Console.WriteLine("   1. Create Disposition records with matching names");
                Console.WriteLine("   2. Re-run import or update existing tickets with correct disposition IDs\n");
            }

            if (_unmappedCourts.Count == 0 && _unmappedOffences.Count == 0 && _unmappedDispositions.Count == 0)
            {
                Console.WriteLine("✅ All courts, offences, and dispositions mapped successfully!\n");
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