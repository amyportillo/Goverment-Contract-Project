# GovContractsApp

This program connects to the live SAM.gov API, retrieves JSON data, stores the raw response in a PostgreSQL JSONB column, and logs fetch timestamps for auditing and ETL traceability.

## Prerequisites
- .NET 10 runtime/SDK (verify with `dotnet --list-runtimes`)
- PostgreSQL instance reachable from your machine
- SAM.gov API key

## Configuration
Set the following environment variables (or edit `Program.cs` for local testing):

- `SAM_API_KEY` – your SAM.gov API key
- `GOV_CONTRACTS_DB` – PostgreSQL connection string (defaults to `Host=localhost;Port=5432;Username=postgres;Password=094825;Database=gov_contracts_dw`)
- `SAM_POSTED_FROM` / `SAM_POSTED_TO` – optional date filters (defaults to the last 7 days, format `yyyy-MM-dd`)

On macOS/Linux you can export them in the shell before running the app:

```bash
export SAM_API_KEY=""
export GOV_CONTRACTS_DB="Host=localhost;Port=5432;Username=postgres;Password="";Database=gov_contracts_dw"
export SAM_POSTED_FROM="2026-02-15"
export SAM_POSTED_TO="2026-02-22"
```

## Run the ETL fetcher
```bash
dotnet run --project "GovContractsApp/GovContractsApp.csproj"
```

## What it does
1. Calls `https://api.sam.gov/opportunities/v2/search` with your API key.
2. Prints the HTTP status and a preview of the JSON payload.
3. Ensures two tables exist:
   - `raw_api_data` for the JSONB payloads plus status metadata.
   - `api_fetch_audit` for timestamped fetch logs (source, status code, posted date range, success flag).
4. Inserts the latest payload into `raw_api_data` and records the attempt in `api_fetch_audit`.
5. Reports the total number of raw records stored so far.

These tables give you both the raw data lake feed and an immutable audit trail for downstream ETL jobs.
