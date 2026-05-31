using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;

namespace HTADataImport
{
    /// <summary>
    /// Prepares Garner/HTA master data in the LegalShark destination database.
    ///
    /// Run this class BEFORE HTADataImporter.
    /// Default StoreId is 9 because Garner import is intended for StoreId = 9.
    /// </summary>
    public class HTAMasterDataSetup
    {
        private readonly string _connectionString;
        private readonly string _sourceDatabaseName;
        private readonly int _storeId;
        private readonly bool _dryRun;
        private readonly bool _clearExistingStoreMasterData;
        private readonly bool _clearGlobalDispositionData;

        public HTAMasterDataSetup(
            string connectionString,
            string sourceDatabaseName = "LegalSharkDB",
            int storeId = 9,
            bool dryRun = true,
            bool clearExistingStoreMasterData = false,
            bool clearGlobalDispositionData = false)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _sourceDatabaseName = string.IsNullOrWhiteSpace(sourceDatabaseName) ? "LegalSharkDB" : sourceDatabaseName.Trim();
            _storeId = storeId;
            _dryRun = dryRun;
            _clearExistingStoreMasterData = clearExistingStoreMasterData;
            _clearGlobalDispositionData = clearGlobalDispositionData;
        }

        public MasterDataSetupResult SetupMasterData()
        {
            if (_storeId <= 0)
                throw new InvalidOperationException("StoreId must be greater than 0.");

            var result = new MasterDataSetupResult();

            using var connection = new SqlConnection(_connectionString);
            connection.Open();

            Console.WriteLine("=======================================");
            Console.WriteLine(" HTA MASTER DATA SETUP STARTED");
            Console.WriteLine("=======================================");
            Console.WriteLine($"Source DB                         : {_sourceDatabaseName}");
            Console.WriteLine($"Destination StoreId               : {_storeId}");
            Console.WriteLine($"DryRun                            : {_dryRun}");
            Console.WriteLine($"Clear Existing Store Master Data  : {_clearExistingStoreMasterData}");
            Console.WriteLine($"Clear Global Disposition Data     : {_clearGlobalDispositionData}");
            Console.WriteLine();

            if (_clearExistingStoreMasterData)
                ClearExistingMasterDataForStore(connection, result);

            EnsureBasicDefaults(connection, result);

            SetupSources(connection, result);
            SetupCourtJurisdiction(connection, result);
            SetupCourtLocations(connection, result);
            SetupAreaOfPractice(connection, result);
            SetupOffenceTypes(connection, result);
            SetupOfficers(connection, result);
            SetupDispositions(connection, result);
            SetupBillingCompanies(connection, result);

            Console.WriteLine();
            Console.WriteLine("=======================================");
            Console.WriteLine(" HTA MASTER DATA SETUP COMPLETED");
            Console.WriteLine("=======================================");
            Console.WriteLine($"Sources processed       : {result.SourcesProcessed}");
            Console.WriteLine($"Courts processed        : {result.CourtsProcessed}");
            Console.WriteLine($"Offences processed      : {result.OffencesProcessed}");
            Console.WriteLine($"Officers processed      : {result.OfficersProcessed}");
            Console.WriteLine($"Dispositions processed  : {result.DispositionsProcessed}");
            Console.WriteLine($"BillingCos processed    : {result.BillingCompaniesProcessed}");
            Console.WriteLine($"Rows deleted            : {result.RowsDeleted}");
            Console.WriteLine($"Warnings                : {result.Warnings.Count}");

            foreach (var warning in result.Warnings)
                Console.WriteLine("WARNING: " + warning);

            return result;
        }

        private void ClearExistingMasterDataForStore(SqlConnection connection, MasterDataSetupResult result)
        {
            Console.WriteLine("=======================================");
            Console.WriteLine($" CLEARING EXISTING MASTER DATA FOR STORE {_storeId}");
            Console.WriteLine("=======================================");

            if (_dryRun)
            {
                Console.WriteLine($"[DRY RUN] Would clear existing master data for StoreId = {_storeId}");
                Console.WriteLine("[DRY RUN] Tables: CourthouseRoom, CourtLocation, CourtJurisdiction, OffenceType, Officer, Source, AreaOfPractice");

                if (_clearGlobalDispositionData)
                    Console.WriteLine("[DRY RUN] Would also clear global Disposition table.");
                else
                    Console.WriteLine("[DRY RUN] Disposition will NOT be cleared because it has no StoreId.");

                Console.WriteLine();
                return;
            }

            using var transaction = connection.BeginTransaction();

            try
            {
                // Delete children first.
                result.RowsDeleted += ExecuteDelete(connection, transaction, @"
DELETE CR
FROM [CourthouseRoom] CR
INNER JOIN [CourtLocation] CL ON CL.[Id] = CR.[CourtId]
WHERE CR.[StoreId] = @StoreId
AND CL.[StoreId] = @StoreId;", "CourthouseRoom");

                // Optional cleanup in case calendar events are linked to old court locations.
                // We null CourtLocationId instead of deleting calendar events.
                result.RowsUpdated += ExecuteUpdate(connection, transaction, @"
UPDATE CE
SET CE.[CourtLocationId] = NULL,
    CE.[UpdatedOnUtc] = GETUTCDATE()
FROM [CalendarEvent] CE
INNER JOIN [CourtLocation] CL ON CL.[Id] = CE.[CourtLocationId]
WHERE CE.[StoreId] = @StoreId
AND CL.[StoreId] = @StoreId;", "CalendarEvent.CourtLocationId");

                result.RowsDeleted += ExecuteDelete(connection, transaction, @"
DELETE FROM [CourtLocation]
WHERE [StoreId] = @StoreId;", "CourtLocation");

                result.RowsDeleted += ExecuteDelete(connection, transaction, @"
DELETE FROM [CourtJurisdiction]
WHERE [StoreId] = @StoreId;", "CourtJurisdiction");

                result.RowsDeleted += ExecuteDelete(connection, transaction, @"
DELETE FROM [OffenceType]
WHERE [StoreId] = @StoreId;", "OffenceType");

                result.RowsDeleted += ExecuteDelete(connection, transaction, @"
DELETE FROM [Officer]
WHERE [StoreId] = @StoreId;", "Officer");

                result.RowsDeleted += ExecuteDelete(connection, transaction, @"
DELETE FROM [Source]
WHERE [StoreId] = @StoreId;", "Source");

                result.RowsDeleted += ExecuteDelete(connection, transaction, @"
DELETE FROM [BillingCompany]
WHERE [StoreId] = @StoreId;", "BillingCompany");

                result.RowsDeleted += ExecuteDelete(connection, transaction, @"
DELETE FROM [AreaOfPractice]
WHERE [StoreId] = @StoreId;", "AreaOfPractice");

                if (_clearGlobalDispositionData)
                {
                    result.RowsDeleted += ExecuteDeleteWithoutStoreId(connection, transaction, @"
DELETE FROM [Disposition];", "Disposition");
                }
                else
                {
                    Console.WriteLine("Skipped Disposition cleanup because Disposition has no StoreId.");
                }

                transaction.Commit();
                Console.WriteLine($"Master data cleanup completed for StoreId = {_storeId}.");
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                transaction.Rollback();

                result.Warnings.Add("Cleanup failed and transaction was rolled back. " + ex.Message);
                throw;
            }
        }

        private void EnsureBasicDefaults(SqlConnection connection, MasterDataSetupResult result)
        {
            Console.WriteLine("Checking basic required defaults...");

            EnsureSource(connection, "Data Import", "Default source for imported HTA/Garner data");
            EnsureAreaOfPractice(connection, "Highway Traffic Act", "Imported HTA / traffic matters");
            EnsureCourtJurisdiction(connection, "Ontario Court of Justice", "Ontario", 1);
            EnsureClientRole(connection);

            Console.WriteLine("Basic defaults checked.");
            Console.WriteLine();
        }

        private void SetupSources(SqlConnection connection, MasterDataSetupResult result)
        {
            Console.WriteLine("Setting up Source master data...");

            var sql = $@"
SELECT DISTINCT
    LTRIM(RTRIM([Source])) AS [SourceName]
FROM [{_sourceDatabaseName}].[dbo].[GarnertblSource]
WHERE NULLIF(LTRIM(RTRIM([Source])), '') IS NOT NULL
ORDER BY LTRIM(RTRIM([Source]));";

            var sourceNames = new List<string>();

            using (var cmd = new SqlCommand(sql, connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var name = reader["SourceName"]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                        sourceNames.Add(name);
                }
            }

            foreach (var sourceName in sourceNames)
            {
                EnsureSource(connection, sourceName, "Imported from Garner source master");
                result.SourcesProcessed++;
            }

            Console.WriteLine($"Sources processed: {result.SourcesProcessed}");
            Console.WriteLine();
        }

        private void SetupCourtJurisdiction(SqlConnection connection, MasterDataSetupResult result)
        {
            Console.WriteLine("Setting up CourtJurisdiction master data...");

            EnsureCourtJurisdiction(connection, "Ontario Court of Justice", "Ontario", 1);

            Console.WriteLine("CourtJurisdiction checked.");
            Console.WriteLine();
        }

        private void SetupCourtLocations(SqlConnection connection, MasterDataSetupResult result)
        {
            Console.WriteLine("Setting up CourtLocation master data...");

            var jurisdictionId = GetCourtJurisdictionId(connection, "Ontario Court of Justice");

            if (jurisdictionId <= 0)
            {
                result.Warnings.Add("CourtJurisdiction 'Ontario Court of Justice' was not found. CourtLocation setup skipped.");
                return;
            }

            var sql = $@"
SELECT DISTINCT
    LTRIM(RTRIM(C.[pkCourtID])) AS [SourceCourtId],
    LTRIM(RTRIM(C.[CourtName])) AS [CourtName],
    LTRIM(RTRIM(C.[Address1])) AS [Address1],
    LTRIM(RTRIM(C.[Address2])) AS [Address2],
    LTRIM(RTRIM(C.[City])) AS [City],
    LTRIM(RTRIM(C.[PostalCode])) AS [PostalCode],
    LTRIM(RTRIM(I.[ICON])) AS [IconCode]
FROM [{_sourceDatabaseName}].[dbo].[GarnertblCourt] C
LEFT JOIN [{_sourceDatabaseName}].[dbo].[GarnertblIcon] I
    ON TRY_CONVERT(INT, C.[fkIconID]) = TRY_CONVERT(INT, I.[pkIconID])
WHERE NULLIF(LTRIM(RTRIM(C.[CourtName])), '') IS NOT NULL
ORDER BY LTRIM(RTRIM(C.[CourtName]));";

            var courts = new List<CourtImportRow>();

            using (var cmd = new SqlCommand(sql, connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    courts.Add(new CourtImportRow
                    {
                        SourceCourtId = reader["SourceCourtId"]?.ToString()?.Trim(),
                        Name = reader["CourtName"]?.ToString()?.Trim(),
                        Address1 = reader["Address1"]?.ToString()?.Trim(),
                        Address2 = reader["Address2"]?.ToString()?.Trim(),
                        City = reader["City"]?.ToString()?.Trim(),
                        PostalCode = reader["PostalCode"]?.ToString()?.Trim(),
                        IconCode = reader["IconCode"]?.ToString()?.Trim()
                    });
                }
            }

            foreach (var court in courts)
            {
                if (string.IsNullOrWhiteSpace(court.Name))
                    continue;

                var address = BuildAddress(court.Address1, court.Address2, court.City, court.PostalCode);

                EnsureCourtLocation(
                    connection,
                    court.Name,
                    jurisdictionId,
                    address,
                    court.IconCode,
                    court.SourceCourtId);

                result.CourtsProcessed++;
            }

            Console.WriteLine($"Court locations processed: {result.CourtsProcessed}");
            Console.WriteLine();
        }

        private void SetupAreaOfPractice(SqlConnection connection, MasterDataSetupResult result)
        {
            Console.WriteLine("Setting up AreaOfPractice master data...");

            EnsureAreaOfPractice(connection, "Highway Traffic Act", "Imported HTA / traffic matters");

            Console.WriteLine("AreaOfPractice checked.");
            Console.WriteLine();
        }

        private void SetupOffenceTypes(SqlConnection connection, MasterDataSetupResult result)
        {
            Console.WriteLine("Setting up OffenceType master data...");

            var areaOfPracticeId = GetAreaOfPracticeId(connection, "Highway Traffic Act");

            if (areaOfPracticeId <= 0)
            {
                result.Warnings.Add("AreaOfPractice 'Highway Traffic Act' was not found. OffenceType setup skipped.");
                return;
            }

            var sql = $@"
SELECT DISTINCT
    LTRIM(RTRIM(OS.[pkOffenseSectionID])) AS [OffenseSectionId],
    LTRIM(RTRIM(OS.[SectionNumber])) AS [SectionNumber],
    LTRIM(RTRIM(OS.[ShortForm])) AS [SectionShortForm],
    LTRIM(RTRIM(OS.[Points])) AS [Points],
    LTRIM(RTRIM(OW.[pkOffenseWordingID])) AS [OffenseWordingId],
    LTRIM(RTRIM(OW.[Description])) AS [WordingDescription],
    LTRIM(RTRIM(OW.[SetFine])) AS [SetFine],
    LTRIM(RTRIM(OW.[SuggestedFee])) AS [SuggestedFee]
FROM [{_sourceDatabaseName}].[dbo].[GarnertblOffenseSection] OS
LEFT JOIN [{_sourceDatabaseName}].[dbo].[GarnertblOffenseWording] OW
    ON TRY_CONVERT(INT, OW.[fkOffenseID]) = TRY_CONVERT(INT, OS.[pkOffenseSectionID])
WHERE
    NULLIF(LTRIM(RTRIM(OS.[SectionNumber])), '') IS NOT NULL
    OR NULLIF(LTRIM(RTRIM(OW.[Description])), '') IS NOT NULL
ORDER BY
    LTRIM(RTRIM(OS.[SectionNumber])),
    LTRIM(RTRIM(OW.[Description]));";

            var offences = new List<OffenceImportRow>();

            using (var cmd = new SqlCommand(sql, connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    offences.Add(new OffenceImportRow
                    {
                        OffenseSectionId = reader["OffenseSectionId"]?.ToString()?.Trim(),
                        SectionNumber = reader["SectionNumber"]?.ToString()?.Trim(),
                        SectionShortForm = reader["SectionShortForm"]?.ToString()?.Trim(),
                        Points = reader["Points"]?.ToString()?.Trim(),
                        OffenseWordingId = reader["OffenseWordingId"]?.ToString()?.Trim(),
                        WordingDescription = reader["WordingDescription"]?.ToString()?.Trim(),
                        SuggestedFee = reader["SuggestedFee"]?.ToString()?.Trim(),
                        SetFine = reader["SetFine"]?.ToString()?.Trim()
                    });
                }
            }

            foreach (var offence in offences)
            {
                var statute = NullIfEmpty(offence.SectionNumber);
                var name =
                    NullIfEmpty(offence.WordingDescription)
                    ?? NullIfEmpty(offence.SectionShortForm)
                    ?? NullIfEmpty(offence.SectionNumber)
                    ?? "Unknown HTA Offence";

                var defaultFee = ParseDecimalOrNull(offence.SuggestedFee)
                                 ?? ParseDecimalOrNull(offence.SetFine);

                EnsureOffenceType(
                    connection,
                    name,
                    statute,
                    defaultFee,
                    areaOfPracticeId,
                    offence.OffenseSectionId,
                    offence.OffenseWordingId,
                    offence.Points);

                result.OffencesProcessed++;
            }

            Console.WriteLine($"Offence types processed: {result.OffencesProcessed}");
            Console.WriteLine();
        }

        private void SetupOfficers(SqlConnection connection, MasterDataSetupResult result)
        {
            Console.WriteLine("Setting up Officer master data...");

            var sql = $@"
SELECT DISTINCT
    LTRIM(RTRIM([pkOfficerID])) AS [SourceOfficerId],
    LTRIM(RTRIM([BadgeNumber])) AS [BadgeNumber],
    LTRIM(RTRIM([iconnumber])) AS [IconNumber],
    LTRIM(RTRIM([LastName])) AS [LastName],
    LTRIM(RTRIM([FirstName])) AS [FirstName],
    LTRIM(RTRIM([comments])) AS [Comments],
    LTRIM(RTRIM([DivisionNumber])) AS [DivisionNumber],
    LTRIM(RTRIM([ynRetired])) AS [YnRetired]
FROM [{_sourceDatabaseName}].[dbo].[GarnertblOfficer]
WHERE
    NULLIF(LTRIM(RTRIM([BadgeNumber])), '') IS NOT NULL
    OR NULLIF(LTRIM(RTRIM([LastName])), '') IS NOT NULL
    OR NULLIF(LTRIM(RTRIM([FirstName])), '') IS NOT NULL
ORDER BY LTRIM(RTRIM([BadgeNumber]));";

            var officers = new List<OfficerImportRow>();

            using (var cmd = new SqlCommand(sql, connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    officers.Add(new OfficerImportRow
                    {
                        SourceOfficerId = reader["SourceOfficerId"]?.ToString()?.Trim(),
                        BadgeNumber = reader["BadgeNumber"]?.ToString()?.Trim(),
                        IconNumber = reader["IconNumber"]?.ToString()?.Trim(),
                        LastName = reader["LastName"]?.ToString()?.Trim(),
                        FirstName = reader["FirstName"]?.ToString()?.Trim(),
                        Comments = reader["Comments"]?.ToString()?.Trim(),
                        DivisionNumber = reader["DivisionNumber"]?.ToString()?.Trim(),
                        IsRetired = ParseBool(reader["YnRetired"]?.ToString())
                    });
                }
            }

            foreach (var officer in officers)
            {
                EnsureOfficer(connection, officer);
                result.OfficersProcessed++;
            }

            Console.WriteLine($"Officers processed: {result.OfficersProcessed}");
            Console.WriteLine();
        }

        private void SetupDispositions(SqlConnection connection, MasterDataSetupResult result)
        {
            Console.WriteLine("Setting up Disposition master data...");

            var sql = $@"
SELECT DISTINCT
    TRY_CONVERT(FLOAT, [pkDispositionID]) AS [SourceDispositionId],
    LTRIM(RTRIM([Description])) AS [Description]
FROM [{_sourceDatabaseName}].[dbo].[GarnertblDisposition]
WHERE NULLIF(LTRIM(RTRIM([Description])), '') IS NOT NULL
ORDER BY LTRIM(RTRIM([Description]));";

            var dispositions = new List<DispositionImportRow>();

            using (var cmd = new SqlCommand(sql, connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    dispositions.Add(new DispositionImportRow
                    {
                        SourceDispositionId = reader["SourceDispositionId"] == DBNull.Value
                            ? null
                            : Convert.ToDouble(reader["SourceDispositionId"]),
                        Description = reader["Description"]?.ToString()?.Trim()
                    });
                }
            }

            foreach (var disposition in dispositions)
            {
                if (string.IsNullOrWhiteSpace(disposition.Description))
                    continue;

                EnsureDisposition(connection, disposition);
                result.DispositionsProcessed++;
            }

            Console.WriteLine($"Dispositions processed: {result.DispositionsProcessed}");
            Console.WriteLine();
        }

        private void SetupBillingCompanies(SqlConnection connection, MasterDataSetupResult result)
        {
            Console.WriteLine("Setting up BillingCompany master data...");

            var sql = $@"
SELECT DISTINCT
    LTRIM(RTRIM([pkBillingCompanyID])) AS [SourceBillingCompanyId],
    LTRIM(RTRIM([CompanyName])) AS [CompanyName],
    LTRIM(RTRIM([CompanyAddress])) AS [CompanyAddress],
    LTRIM(RTRIM([ContactName])) AS [ContactName],
    LTRIM(RTRIM([Phone])) AS [Phone],
    LTRIM(RTRIM([Ext])) AS [Ext],
    LTRIM(RTRIM([Fax])) AS [Fax],
    LTRIM(RTRIM([Email])) AS [Email],
    LTRIM(RTRIM([CVORNumber])) AS [CVORNumber],
    LTRIM(RTRIM([Notes])) AS [Notes]
FROM [{_sourceDatabaseName}].[dbo].[GarnertblBillingCompany]
WHERE NULLIF(LTRIM(RTRIM([CompanyName])), '') IS NOT NULL
ORDER BY LTRIM(RTRIM([CompanyName]));";

            var billingCompanies = new List<BillingCompanyImportRow>();

            using (var cmd = new SqlCommand(sql, connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    billingCompanies.Add(new BillingCompanyImportRow
                    {
                        SourceBillingCompanyId = reader["SourceBillingCompanyId"]?.ToString()?.Trim(),
                        CompanyName = reader["CompanyName"]?.ToString()?.Trim(),
                        CompanyAddress = reader["CompanyAddress"]?.ToString()?.Trim(),
                        ContactName = reader["ContactName"]?.ToString()?.Trim(),
                        Phone = reader["Phone"]?.ToString()?.Trim(),
                        Ext = reader["Ext"]?.ToString()?.Trim(),
                        Fax = reader["Fax"]?.ToString()?.Trim(),
                        Email = reader["Email"]?.ToString()?.Trim(),
                        CVORNumber = reader["CVORNumber"]?.ToString()?.Trim(),
                        Notes = reader["Notes"]?.ToString()?.Trim()
                    });
                }
            }

            foreach (var billingCompany in billingCompanies)
            {
                if (string.IsNullOrWhiteSpace(billingCompany.CompanyName))
                    continue;

                EnsureBillingCompany(connection, billingCompany);
                result.BillingCompaniesProcessed++;
            }

            Console.WriteLine($"BillingCompanies processed: {result.BillingCompaniesProcessed}");
            Console.WriteLine();
        }

        private void EnsureSource(SqlConnection connection, string name, string description)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            var existsSql = @"
SELECT TOP 1 [Id]
FROM [Source]
WHERE [StoreId] = @StoreId
AND LTRIM(RTRIM([Name])) = LTRIM(RTRIM(@Name));";

            using (var existsCmd = new SqlCommand(existsSql, connection))
            {
                existsCmd.Parameters.AddWithValue("@StoreId", _storeId);
                existsCmd.Parameters.AddWithValue("@Name", name);

                var existingId = existsCmd.ExecuteScalar();
                if (existingId != null && existingId != DBNull.Value)
                    return;
            }

            var insertSql = @"
INSERT INTO [Source]
(
    [StoreId],
    [Name],
    [Description],
    [IsActive],
    [CreatedOnUtc],
    [UpdatedOnUtc]
)
VALUES
(
    @StoreId,
    @Name,
    @Description,
    1,
    GETUTCDATE(),
    NULL
);";

            ExecuteNonQuery(connection, insertSql, cmd =>
            {
                cmd.Parameters.AddWithValue("@StoreId", _storeId);
                cmd.Parameters.AddWithValue("@Name", name);
                cmd.Parameters.AddWithValue("@Description", (object?)description ?? DBNull.Value);
            }, $"Would insert Source: {name}");
        }

        private void EnsureAreaOfPractice(SqlConnection connection, string name, string description)
        {
            var existsSql = @"
SELECT TOP 1 [Id]
FROM [AreaOfPractice]
WHERE [StoreId] = @StoreId
AND LTRIM(RTRIM([Name])) = LTRIM(RTRIM(@Name));";

            using (var existsCmd = new SqlCommand(existsSql, connection))
            {
                existsCmd.Parameters.AddWithValue("@StoreId", _storeId);
                existsCmd.Parameters.AddWithValue("@Name", name);

                var existingId = existsCmd.ExecuteScalar();
                if (existingId != null && existingId != DBNull.Value)
                    return;
            }

            var insertSql = @"
INSERT INTO [AreaOfPractice]
(
    [StoreId],
    [Name],
    [Description],
    [DisplayOrder],
    [IsActive],
    [CreatedOnUtc],
    [UpdatedOnUtc]
)
VALUES
(
    @StoreId,
    @Name,
    @Description,
    0,
    1,
    GETUTCDATE(),
    NULL
);";

            ExecuteNonQuery(connection, insertSql, cmd =>
            {
                cmd.Parameters.AddWithValue("@StoreId", _storeId);
                cmd.Parameters.AddWithValue("@Name", name);
                cmd.Parameters.AddWithValue("@Description", (object?)description ?? DBNull.Value);
            }, $"Would insert AreaOfPractice: {name}");
        }

        private void EnsureCourtJurisdiction(SqlConnection connection, string name, string governingBody, int level)
        {
            var existsSql = @"
SELECT TOP 1 [Id]
FROM [CourtJurisdiction]
WHERE [StoreId] = @StoreId
AND LTRIM(RTRIM([Name])) = LTRIM(RTRIM(@Name));";

            using (var existsCmd = new SqlCommand(existsSql, connection))
            {
                existsCmd.Parameters.AddWithValue("@StoreId", _storeId);
                existsCmd.Parameters.AddWithValue("@Name", name);

                var existingId = existsCmd.ExecuteScalar();
                if (existingId != null && existingId != DBNull.Value)
                    return;
            }

            var insertSql = @"
INSERT INTO [CourtJurisdiction]
(
    [StoreId],
    [Name],
    [GoverningBody],
    [Level],
    [IsActive],
    [CreatedOnUtc],
    [UpdatedOnUtc]
)
VALUES
(
    @StoreId,
    @Name,
    @GoverningBody,
    @Level,
    1,
    GETUTCDATE(),
    NULL
);";

            ExecuteNonQuery(connection, insertSql, cmd =>
            {
                cmd.Parameters.AddWithValue("@StoreId", _storeId);
                cmd.Parameters.AddWithValue("@Name", name);
                cmd.Parameters.AddWithValue("@GoverningBody", governingBody);
                cmd.Parameters.AddWithValue("@Level", level);
            }, $"Would insert CourtJurisdiction: {name}");
        }

        private void EnsureCourtLocation(
            SqlConnection connection,
            string name,
            int courtJurisdictionId,
            string? address,
            string? iconCode,
            string? sourceCourtId)
        {
            var existsSql = @"
SELECT TOP 1 [Id]
FROM [CourtLocation]
WHERE [StoreId] = @StoreId
AND
(
    LTRIM(RTRIM([Name])) = LTRIM(RTRIM(@Name))
    OR
    (
        NULLIF(LTRIM(RTRIM(ISNULL([IconCode], ''))), '') IS NOT NULL
        AND NULLIF(LTRIM(RTRIM(ISNULL(@IconCode, ''))), '') IS NOT NULL
        AND LTRIM(RTRIM([IconCode])) = LTRIM(RTRIM(@IconCode))
    )
);";

            using (var existsCmd = new SqlCommand(existsSql, connection))
            {
                existsCmd.Parameters.AddWithValue("@StoreId", _storeId);
                existsCmd.Parameters.AddWithValue("@Name", name);
                existsCmd.Parameters.AddWithValue("@IconCode", (object?)iconCode ?? DBNull.Value);

                var existingId = existsCmd.ExecuteScalar();
                if (existingId != null && existingId != DBNull.Value)
                    return;
            }

            var finalAddress = address;
            if (!string.IsNullOrWhiteSpace(sourceCourtId))
                finalAddress = string.IsNullOrWhiteSpace(finalAddress)
                    ? $"HTA Court ID: {sourceCourtId}"
                    : finalAddress + $" | HTA Court ID: {sourceCourtId}";

            var insertSql = @"
INSERT INTO [CourtLocation]
(
    [StoreId],
    [Name],
    [CourtJurisdictionId],
    [Address],
    [IsVirtual],
    [DefaultCalendarRules],
    [IsActive],
    [CreatedOnUtc],
    [UpdatedOnUtc],
    [IconCode]
)
VALUES
(
    @StoreId,
    @Name,
    @CourtJurisdictionId,
    @Address,
    0,
    NULL,
    1,
    GETUTCDATE(),
    NULL,
    @IconCode
);";

            ExecuteNonQuery(connection, insertSql, cmd =>
            {
                cmd.Parameters.AddWithValue("@StoreId", _storeId);
                cmd.Parameters.AddWithValue("@Name", name);
                cmd.Parameters.AddWithValue("@CourtJurisdictionId", courtJurisdictionId);
                cmd.Parameters.AddWithValue("@Address", (object?)finalAddress ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@IconCode", (object?)iconCode ?? DBNull.Value);
            }, $"Would insert CourtLocation: {name}");
        }

        private void EnsureOffenceType(
            SqlConnection connection,
            string name,
            string? statute,
            decimal? defaultFee,
            int areaOfPracticeId,
            string? sourceOffenseSectionId,
            string? sourceOffenseWordingId,
            string? points)
        {
            var existsSql = @"
SELECT TOP 1 [Id]
FROM [OffenceType]
WHERE [StoreId] = @StoreId
AND ISNULL([AreaOfPracticeId], 0) = ISNULL(@AreaOfPracticeId, 0)
AND LTRIM(RTRIM([Name])) = LTRIM(RTRIM(@Name))
AND ISNULL(LTRIM(RTRIM([Statute])), '') = ISNULL(LTRIM(RTRIM(@Statute)), '');";

            using (var existsCmd = new SqlCommand(existsSql, connection))
            {
                existsCmd.Parameters.AddWithValue("@StoreId", _storeId);
                existsCmd.Parameters.AddWithValue("@AreaOfPracticeId", areaOfPracticeId);
                existsCmd.Parameters.AddWithValue("@Name", name);
                existsCmd.Parameters.AddWithValue("@Statute", (object?)statute ?? DBNull.Value);

                var existingId = existsCmd.ExecuteScalar();
                if (existingId != null && existingId != DBNull.Value)
                    return;
            }

            var notes = BuildOffenceStrategyNotes(sourceOffenseSectionId, sourceOffenseWordingId, points);

            var insertSql = @"
INSERT INTO [OffenceType]
(
    [StoreId],
    [Name],
    [Statute],
    [Severity],
    [DefaultFee],
    [StrategyNotes],
    [IsActive],
    [CreatedOnUtc],
    [UpdatedOnUtc],
    [AreaOfPracticeId]
)
VALUES
(
    @StoreId,
    @Name,
    @Statute,
    1,
    @DefaultFee,
    @StrategyNotes,
    1,
    GETUTCDATE(),
    NULL,
    @AreaOfPracticeId
);";

            ExecuteNonQuery(connection, insertSql, cmd =>
            {
                cmd.Parameters.AddWithValue("@StoreId", _storeId);
                cmd.Parameters.AddWithValue("@Name", name);
                cmd.Parameters.AddWithValue("@Statute", (object?)statute ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DefaultFee", (object?)defaultFee ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@StrategyNotes", (object?)notes ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@AreaOfPracticeId", areaOfPracticeId);
            }, $"Would insert OffenceType: {name} / {statute}");
        }

        private void EnsureOfficer(SqlConnection connection, OfficerImportRow officer)
        {
            if (string.IsNullOrWhiteSpace(officer.BadgeNumber)
                && string.IsNullOrWhiteSpace(officer.FirstName)
                && string.IsNullOrWhiteSpace(officer.LastName))
            {
                return;
            }

            var existsSql = string.IsNullOrWhiteSpace(officer.BadgeNumber)
                ? @"
SELECT TOP 1 [Id]
FROM [Officer]
WHERE [StoreId] = @StoreId
AND ISNULL(LTRIM(RTRIM([FirstName])), '') = ISNULL(LTRIM(RTRIM(@FirstName)), '')
AND ISNULL(LTRIM(RTRIM([LastName])), '') = ISNULL(LTRIM(RTRIM(@LastName)), '')
AND ISNULL(LTRIM(RTRIM([DivisionNumber])), '') = ISNULL(LTRIM(RTRIM(@DivisionNumber)), '');"
                : @"
SELECT TOP 1 [Id]
FROM [Officer]
WHERE [StoreId] = @StoreId
AND NULLIF(LTRIM(RTRIM(ISNULL([BadgeNumber], ''))), '') IS NOT NULL
AND LTRIM(RTRIM([BadgeNumber])) = LTRIM(RTRIM(@BadgeNumber));";

            using (var existsCmd = new SqlCommand(existsSql, connection))
            {
                existsCmd.Parameters.AddWithValue("@StoreId", _storeId);
                existsCmd.Parameters.AddWithValue("@BadgeNumber", (object?)officer.BadgeNumber ?? DBNull.Value);
                existsCmd.Parameters.AddWithValue("@FirstName", (object?)officer.FirstName ?? DBNull.Value);
                existsCmd.Parameters.AddWithValue("@LastName", (object?)officer.LastName ?? DBNull.Value);
                existsCmd.Parameters.AddWithValue("@DivisionNumber", (object?)officer.DivisionNumber ?? DBNull.Value);

                var existingId = existsCmd.ExecuteScalar();
                if (existingId != null && existingId != DBNull.Value)
                    return;
            }

            var comments = officer.Comments;
            if (!string.IsNullOrWhiteSpace(officer.SourceOfficerId))
                comments = string.IsNullOrWhiteSpace(comments)
                    ? $"HTA Officer ID: {officer.SourceOfficerId}"
                    : comments + $" | HTA Officer ID: {officer.SourceOfficerId}";

            var insertSql = @"
INSERT INTO [Officer]
(
    [StoreId],
    [BadgeNumber],
    [IconNumber],
    [LastName],
    [FirstName],
    [Comments],
    [DivisionNumber],
    [IsRetired],
    [IsActive],
    [CreatedOnUtc],
    [UpdatedOnUtc]
)
VALUES
(
    @StoreId,
    @BadgeNumber,
    @IconNumber,
    @LastName,
    @FirstName,
    @Comments,
    @DivisionNumber,
    @IsRetired,
    1,
    GETUTCDATE(),
    NULL
);";

            ExecuteNonQuery(connection, insertSql, cmd =>
            {
                cmd.Parameters.AddWithValue("@StoreId", _storeId);
                cmd.Parameters.AddWithValue("@BadgeNumber", (object?)officer.BadgeNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@IconNumber", (object?)officer.IconNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@LastName", (object?)officer.LastName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@FirstName", (object?)officer.FirstName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Comments", (object?)comments ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DivisionNumber", (object?)officer.DivisionNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@IsRetired", officer.IsRetired);
            }, $"Would insert Officer: {officer.BadgeNumber} {officer.FirstName} {officer.LastName}");
        }

        private void EnsureDisposition(SqlConnection connection, DispositionImportRow disposition)
        {
            if (string.IsNullOrWhiteSpace(disposition.Description))
                return;

            var existsSql = @"
SELECT TOP 1 [ID]
FROM [Disposition]
WHERE LTRIM(RTRIM([Description])) = LTRIM(RTRIM(@Description));";

            using (var existsCmd = new SqlCommand(existsSql, connection))
            {
                existsCmd.Parameters.AddWithValue("@Description", disposition.Description);

                var existingId = existsCmd.ExecuteScalar();
                if (existingId != null && existingId != DBNull.Value)
                    return;
            }

            var nextId = GetNextDispositionId(connection);

            var insertSql = @"
INSERT INTO [Disposition]
(
    [ID],
    [pkDispositionID],
    [Description]
)
VALUES
(
    @ID,
    @pkDispositionID,
    @Description
);";

            ExecuteNonQuery(connection, insertSql, cmd =>
            {
                cmd.Parameters.AddWithValue("@ID", nextId);
                cmd.Parameters.AddWithValue("@pkDispositionID", (object?)disposition.SourceDispositionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Description", disposition.Description);
            }, $"Would insert Disposition: {disposition.Description}");
        }

        private void EnsureBillingCompany(SqlConnection connection, BillingCompanyImportRow billingCompany)
        {
            if (string.IsNullOrWhiteSpace(billingCompany.CompanyName))
                return;

            var existsSql = @"
SELECT TOP 1 [Id]
FROM [BillingCompany]
WHERE [StoreId] = @StoreId
AND LTRIM(RTRIM([CompanyName])) = LTRIM(RTRIM(@CompanyName));";

            using (var existsCmd = new SqlCommand(existsSql, connection))
            {
                existsCmd.Parameters.AddWithValue("@StoreId", _storeId);
                existsCmd.Parameters.AddWithValue("@CompanyName", billingCompany.CompanyName);

                var existingId = existsCmd.ExecuteScalar();
                if (existingId != null && existingId != DBNull.Value)
                    return;
            }

            var insertSql = @"
INSERT INTO [BillingCompany]
(
    [StoreId],
    [CompanyName],
    [ContactName],
    [PhoneNumber],
    [Email],
    [StreetAddress],
    [Notes],
    [IsActive],
    [CreatedOnUtc],
    [UpdatedOnUtc]
)
VALUES
(
    @StoreId,
    @CompanyName,
    @ContactName,
    @PhoneNumber,
    @Email,
    @StreetAddress,
    @Notes,
    1,
    GETUTCDATE(),
    NULL
);";

            var phoneNumber = billingCompany.Phone;
            if (!string.IsNullOrWhiteSpace(billingCompany.Ext))
                phoneNumber = $"{phoneNumber} ext. {billingCompany.Ext}";

            ExecuteNonQuery(connection, insertSql, cmd =>
            {
                cmd.Parameters.AddWithValue("@StoreId", _storeId);
                cmd.Parameters.AddWithValue("@CompanyName", billingCompany.CompanyName);
                cmd.Parameters.AddWithValue("@ContactName", (object?)billingCompany.ContactName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@PhoneNumber", (object?)phoneNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Email", (object?)billingCompany.Email ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@StreetAddress", (object?)billingCompany.CompanyAddress ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Notes", (object?)billingCompany.Notes ?? DBNull.Value);
            }, $"Would insert BillingCompany: {billingCompany.CompanyName}");
        }

        private void EnsureClientRole(SqlConnection connection)
        {
            var existsSql = @"
SELECT TOP 1 [Id]
FROM [CustomerRole]
WHERE [Name] = 'Client'
   OR [SystemName] = 'Client';";

            using (var existsCmd = new SqlCommand(existsSql, connection))
            {
                var existingId = existsCmd.ExecuteScalar();
                if (existingId != null && existingId != DBNull.Value)
                    return;
            }

            var insertSql = @"
INSERT INTO [CustomerRole]
(
    [Name],
    [SystemName],
    [FreeShipping],
    [TaxExempt],
    [Active],
    [IsSystemRole],
    [EnablePasswordLifetime],
    [OverrideTaxDisplayType],
    [DefaultTaxDisplayTypeId],
    [PurchasedWithProductId]
)
VALUES
(
    'Client',
    'Client',
    0,
    0,
    1,
    0,
    0,
    0,
    0,
    0
);";

            ExecuteNonQuery(connection, insertSql, _ => { }, "Would insert CustomerRole: Client");
        }

        private int GetCourtJurisdictionId(SqlConnection connection, string name)
        {
            var sql = @"
SELECT TOP 1 [Id]
FROM [CourtJurisdiction]
WHERE [StoreId] = @StoreId
AND LTRIM(RTRIM([Name])) = LTRIM(RTRIM(@Name));";

            using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@StoreId", _storeId);
            cmd.Parameters.AddWithValue("@Name", name);

            var value = cmd.ExecuteScalar();
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
        }

        private int GetAreaOfPracticeId(SqlConnection connection, string name)
        {
            var sql = @"
SELECT TOP 1 [Id]
FROM [AreaOfPractice]
WHERE [StoreId] = @StoreId
AND LTRIM(RTRIM([Name])) = LTRIM(RTRIM(@Name));";

            using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@StoreId", _storeId);
            cmd.Parameters.AddWithValue("@Name", name);

            var value = cmd.ExecuteScalar();
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
        }

        private int GetNextDispositionId(SqlConnection connection)
        {
            using var cmd = new SqlCommand("SELECT ISNULL(MAX([ID]), 0) + 1 FROM [Disposition];", connection);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private int ExecuteDelete(SqlConnection connection, SqlTransaction transaction, string sql, string tableName)
        {
            using var cmd = new SqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("@StoreId", _storeId);

            var affected = cmd.ExecuteNonQuery();
            Console.WriteLine($"Deleted {affected} row(s) from {tableName}");
            return affected;
        }

        private int ExecuteDeleteWithoutStoreId(SqlConnection connection, SqlTransaction transaction, string sql, string tableName)
        {
            using var cmd = new SqlCommand(sql, connection, transaction);

            var affected = cmd.ExecuteNonQuery();
            Console.WriteLine($"Deleted {affected} row(s) from {tableName}");
            return affected;
        }

        private int ExecuteUpdate(SqlConnection connection, SqlTransaction transaction, string sql, string targetName)
        {
            using var cmd = new SqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("@StoreId", _storeId);

            var affected = cmd.ExecuteNonQuery();
            Console.WriteLine($"Updated {affected} row(s) for {targetName}");
            return affected;
        }

        private void ExecuteNonQuery(SqlConnection connection, string sql, Action<SqlCommand> addParameters, string dryRunMessage)
        {
            if (_dryRun)
            {
                Console.WriteLine("[DRY RUN] " + dryRunMessage);
                return;
            }

            using var cmd = new SqlCommand(sql, connection);
            addParameters(cmd);
            cmd.ExecuteNonQuery();
        }

        private static string? BuildAddress(string? address1, string? address2, string? city, string? postalCode)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(address1))
                parts.Add(address1.Trim());

            if (!string.IsNullOrWhiteSpace(address2))
                parts.Add(address2.Trim());

            if (!string.IsNullOrWhiteSpace(city))
                parts.Add(city.Trim());

            if (!string.IsNullOrWhiteSpace(postalCode))
                parts.Add(postalCode.Trim());

            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        private static string? BuildOffenceStrategyNotes(string? sectionId, string? wordingId, string? points)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(sectionId))
                parts.Add("HTA OffenseSection ID: " + sectionId.Trim());

            if (!string.IsNullOrWhiteSpace(wordingId))
                parts.Add("HTA OffenseWording ID: " + wordingId.Trim());

            if (!string.IsNullOrWhiteSpace(points))
                parts.Add("Points: " + points.Trim());

            return parts.Count == 0 ? null : string.Join(" | ", parts);
        }

        private static string? NullIfEmpty(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static decimal? ParseDecimalOrNull(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Replace("$", "").Replace(",", "").Trim();

            return decimal.TryParse(value, out var result) ? result : null;
        }

        private static bool ParseBool(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim().ToLowerInvariant();

            return value == "1"
                   || value == "true"
                   || value == "yes"
                   || value == "y";
        }

        private class CourtImportRow
        {
            public string? SourceCourtId { get; set; }
            public string? Name { get; set; }
            public string? Address1 { get; set; }
            public string? Address2 { get; set; }
            public string? City { get; set; }
            public string? PostalCode { get; set; }
            public string? IconCode { get; set; }
        }

        private class OffenceImportRow
        {
            public string? OffenseSectionId { get; set; }
            public string? SectionNumber { get; set; }
            public string? SectionShortForm { get; set; }
            public string? Points { get; set; }
            public string? OffenseWordingId { get; set; }
            public string? WordingDescription { get; set; }
            public string? SuggestedFee { get; set; }
            public string? SetFine { get; set; }
        }

        private class OfficerImportRow
        {
            public string? SourceOfficerId { get; set; }
            public string? BadgeNumber { get; set; }
            public string? IconNumber { get; set; }
            public string? LastName { get; set; }
            public string? FirstName { get; set; }
            public string? Comments { get; set; }
            public string? DivisionNumber { get; set; }
            public bool IsRetired { get; set; }
        }

        private class DispositionImportRow
        {
            public double? SourceDispositionId { get; set; }
            public string? Description { get; set; }
        }

        private class BillingCompanyImportRow
        {
            public string? SourceBillingCompanyId { get; set; }
            public string? CompanyName { get; set; }
            public string? CompanyAddress { get; set; }
            public string? ContactName { get; set; }
            public string? Phone { get; set; }
            public string? Ext { get; set; }
            public string? Fax { get; set; }
            public string? Email { get; set; }
            public string? CVORNumber { get; set; }
            public string? Notes { get; set; }
        }
    }

    public class MasterDataSetupResult
    {
        public int SourcesProcessed { get; set; }
        public int CourtsProcessed { get; set; }
        public int OffencesProcessed { get; set; }
        public int OfficersProcessed { get; set; }
        public int DispositionsProcessed { get; set; }
        public int BillingCompaniesProcessed { get; set; }
        public int RowsDeleted { get; set; }
        public int RowsUpdated { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
