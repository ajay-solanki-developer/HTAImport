# HTA Data Import Tool

Standalone tool to import customer and ticket data from HTA Pro CSV export into NopCommerce database.

## Prerequisites

- .NET 7.0 SDK or later
- SQL Server database (NopCommerce)
- HTA Pro CSV export file

## Setup

1. Update `appsettings.json` with your configuration:
   - **ConnectionString**: Your SQL Server connection string
   - **CsvFilePath**: Full path to your CSV file
   - **DryRun**: Set to `true` to test, `false` to actually import

## Running

```bash
# Restore packages (first time only)
dotnet restore

# Build
dotnet build

# Run
dotnet run