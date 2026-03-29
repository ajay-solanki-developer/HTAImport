using System;
using System.IO;
using System.Text.Json;

namespace HTADataImport
{
    class Program
    {
        // "ConnectionString": "Data Source=AJAY20\\SQL22;Initial Catalog=LegalShakDB;Integrated Security=False;Persist Security Info=False;User ID=sa;Password=Ajay@Sql20;Trust Server Certificate=True",

        static void Main(string[] args)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("═══════════════════════════════════");
            Console.WriteLine("   HTA Pro Data Import Tool");
            Console.WriteLine("═══════════════════════════════════");
            Console.ResetColor();
            Console.WriteLine();

            try
            {
                // Load configuration
                var config = LoadConfiguration();
                if (config == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("✗ ERROR: Could not load appsettings.json");
                    Console.ResetColor();
                    Console.WriteLine("\nPress any key to exit...");
                    Console.ReadKey();
                    return;
                }

                // Validate configuration
                if (string.IsNullOrEmpty(config.ConnectionString))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("✗ ERROR: Connection string not found in appsettings.json");
                    Console.ResetColor();
                    Console.WriteLine("\nPress any key to exit...");
                    Console.ReadKey();
                    return;
                }

                // Display configuration
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"Connection String: {MaskConnectionString(config.ConnectionString)}");
                Console.WriteLine($"Store ID: {config.StoreId}");
                Console.WriteLine($"Firm Name: {config.FirmName}");
                Console.WriteLine($"Source: TempAllClientInfo table");
                if (!string.IsNullOrEmpty(config.FilterCsvPath))
                {
                    Console.WriteLine($"Filter: {config.FilterCsvPath} (importing selected tickets only)");
                }
                Console.WriteLine($"Dry Run Mode: {(config.DryRun ? "YES (No data will be imported)" : "NO (Data WILL be imported)")}");
                Console.ResetColor();
                Console.WriteLine();

                if (config.DryRun)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("⚠ DRY RUN MODE ACTIVE - No data will actually be imported!");
                    Console.ResetColor();
                }

                Console.WriteLine("\nPress ENTER to start import or ESC to cancel...");
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Escape)
                {
                    Console.WriteLine("Import cancelled.");
                    return;
                }

                // Create importer
                var importer = new HTADataImporter(config.ConnectionString, config.StoreId, config.FirmName, config.FilterCsvPath, config.DryRun);
                
                // Run import
                Console.WriteLine("\nStarting import...\n");
                var result = importer.Import();
                
                // Display results
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("═══════════════════════════════════");
                Console.WriteLine("   Import Complete!");
                Console.WriteLine("═══════════════════════════════════");
                Console.ResetColor();
                Console.WriteLine($"✓ Customers imported: {result.CustomersImported}");
                Console.WriteLine($"✓ Tickets imported: {result.TicketsImported}");
                Console.WriteLine($"⚠ Warnings: {result.Warnings.Count}");
                Console.WriteLine($"✗ Errors: {result.Errors.Count}");
                
                if (result.Warnings.Count > 0)
                {
                    Console.WriteLine("\nWarnings:");
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    foreach (var warning in result.Warnings.Take(10))
                    {
                        Console.WriteLine($"  ⚠ {warning}");
                    }
                    if (result.Warnings.Count > 10)
                    {
                        Console.WriteLine($"  ... and {result.Warnings.Count - 10} more warnings");
                    }
                    Console.ResetColor();
                }
                
                if (result.Errors.Count > 0)
                {
                    Console.WriteLine("\nErrors encountered:");
                    Console.ForegroundColor = ConsoleColor.Red;
                    foreach (var error in result.Errors.Take(10))
                    {
                        Console.WriteLine($"  ✗ {error}");
                    }
                    if (result.Errors.Count > 10)
                    {
                        Console.WriteLine($"  ... and {result.Errors.Count - 10} more errors");
                    }
                    Console.ResetColor();
                }

                if (config.DryRun)
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("⚠ DRY RUN MODE - No data was actually imported!");
                    Console.WriteLine("Set 'DryRun': false in appsettings.json to perform actual import.");
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("✓ Data has been successfully imported to the database!");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"✗ FATAL ERROR: {ex.Message}");
                Console.ResetColor();
                Console.WriteLine($"\nStack Trace:\n{ex.StackTrace}");
            }
            
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }

        private static AppSettings? LoadConfiguration()
        {
            try
            {
                var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                if (!File.Exists(configPath))
                {
                    Console.WriteLine($"Configuration file not found: {configPath}");
                    return null;
                }

                var json = File.ReadAllText(configPath);
                return JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading configuration: {ex.Message}");
                return null;
            }
        }

        private static string MaskConnectionString(string connectionString)
        {
            if (string.IsNullOrEmpty(connectionString))
                return "***";

            var parts = connectionString.Split(';');
            var masked = new List<string>();
            
            foreach (var part in parts)
            {
                if (part.Trim().StartsWith("Password=", StringComparison.OrdinalIgnoreCase) ||
                    part.Trim().StartsWith("Pwd=", StringComparison.OrdinalIgnoreCase))
                {
                    masked.Add("Password=***");
                }
                else
                {
                    masked.Add(part);
                }
            }
            
            return string.Join(";", masked);
        }
    }

    public class AppSettings
    {
        public string ConnectionString { get; set; } = string.Empty;
        public int StoreId { get; set; } = 1;
        public string FirmName { get; set; } = "HTA Import";
        public string? FilterCsvPath { get; set; } = null;
        public bool DryRun { get; set; } = true;
    }
}