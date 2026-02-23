// Base .NET namespace for console, string, and date utilities
using System;
// Enables formatting dates as specific string patterns
using System.Globalization;
// Gives access to HttpStatusCode enum for readable logging
using System.Net;
// Provides HttpClient so we can call the SAM.gov REST API
using System.Net.Http;
// Allows us to use async/await for non-blocking I/O
using System.Threading.Tasks;
// Npgsql is the official PostgreSQL driver for .NET
using Npgsql;
// Brings in PostgreSQL-specific data types such as JSONB
using NpgsqlTypes;

class Program
{
    // Hard-coded fallback key in case SAM_API_KEY env var is missing (use secrets in production!)
    private const string DefaultApiKey = "APIKEY";
    // Base endpoint for searching opportunities on SAM.gov
    private const string ApiBaseUrl = "https://api.sam.gov/opportunities/v2/search";

    static async Task Main()
    {
        // Try to read an API key from the environment first, otherwise fall back to the constant above
        string apiKey = Environment.GetEnvironmentVariable("SAM_API_KEY") ?? DefaultApiKey;
        // Guard clause that stops execution if we still do not have a key
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("An API key is required. Set the SAM_API_KEY environment variable.");
            return;
        }

        // Build the PostgreSQL connection string (environment first, then default local dev string)
        string connectionString =
            Environment.GetEnvironmentVariable("GOV_CONTRACTS_DB") ??
            "Host=localhost;Port=5432;Username=postgres;Password=094825;Database=gov_contracts_dw";

        // Determine which date range we want to fetch from SAM.gov
        var (postedFrom, postedTo) = GetPostedDateRange();
        // Craft the final HTTPS URL with query parameters encoded
        string apiUrl = BuildSearchUrl(apiKey, postedFrom, postedTo);

        // HttpClient implements IDisposable, so we use the new using declaration syntax
        using HttpClient client = new HttpClient();

        try
        {
            // Verbose console logs make it easier to follow the ETL flow step-by-step
            Console.WriteLine($"Requesting SAM.gov data posted between {postedFrom} and {postedTo}...");
            Console.WriteLine($"GET {apiUrl}");
            Console.WriteLine("Connecting to SAM.gov API...");
            Console.WriteLine("-----------------------------------");

            // Actually call the API and await the HTTP response
            var response = await client.GetAsync(apiUrl);
            // Read the JSON payload as a string (could be large, so we preview a subset later)
            string json = await response.Content.ReadAsStringAsync();

            // Print the status code so we know if the call succeeded
            Console.WriteLine($"Status Code: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine("API request failed.");
            }
            else
            {
                Console.WriteLine("API request successful!");
            }

            // Show the first 500 characters to verify the data looks correct
            Console.WriteLine("\nSample JSON Preview:");
            Console.WriteLine("-----------------------------------");
            int previewLength = Math.Min(json.Length, 500);
            Console.WriteLine(previewLength > 0 ? json[..previewLength] : "<empty response>");
            Console.WriteLine("-----------------------------------");

            // Open the PostgreSQL connection asynchronously to avoid blocking threads
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            Console.WriteLine("Connected to PostgreSQL database.");

            // Make sure tables exist before we try to insert
            await EnsureRawTableExists(conn);
            await EnsureAuditTableExists(conn);
            // Store the raw JSON, then write an audit record, then show totals
            await InsertRawJson(conn, json, response.IsSuccessStatusCode);
            await LogFetchAudit(conn, response.StatusCode, response.IsSuccessStatusCode, postedFrom, postedTo);
            await PrintTotalRows(conn);
        }
        catch (HttpRequestException httpEx)
        {
            Console.WriteLine("HTTP error:");
            Console.WriteLine(httpEx.Message);
        }
        catch (PostgresException pgEx)
        {
            Console.WriteLine("PostgreSQL error:");
            Console.WriteLine(pgEx.MessageText);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Unexpected error:");
            Console.WriteLine(ex.Message);
        }
    }

    // Creates the raw_api_data table on first run so inserts do not fail
    private static async Task EnsureRawTableExists(NpgsqlConnection conn)
    {
        const string tableSql = @"
            CREATE TABLE IF NOT EXISTS raw_api_data (
                id SERIAL PRIMARY KEY,
                source_name TEXT NOT NULL,
                raw_json JSONB NOT NULL,
                status TEXT NOT NULL,
                error_message TEXT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );";

        // Execute the CREATE TABLE statement (IF NOT EXISTS keeps it idempotent)
        await using var cmd = new NpgsqlCommand(tableSql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    // Inserts the latest JSON payload plus metadata into raw_api_data
    private static async Task InsertRawJson(NpgsqlConnection conn, string json, bool success)
    {
        const string insertSql = @"
            INSERT INTO raw_api_data (source_name, raw_json, status, error_message)
            VALUES (@source_name, @raw_json, @status, @error_message);";

        await using var cmd = new NpgsqlCommand(insertSql, conn);
        // All rows come from SAM.gov in this prototype, so the value is hard-coded
        cmd.Parameters.AddWithValue("source_name", "SAM.gov");
        // JSONB parameter keeps the data queryable downstream
        cmd.Parameters.AddWithValue("raw_json", NpgsqlDbType.Jsonb, json);
        cmd.Parameters.AddWithValue("status", success ? "Success" : "Failed");
        cmd.Parameters.AddWithValue("error_message", success ? (object)DBNull.Value : "API call failed");

        int rows = await cmd.ExecuteNonQueryAsync();
        Console.WriteLine($"\nRows inserted: {rows}");
        Console.WriteLine("Raw JSON stored successfully!");
    }

    // Simple helper to show how many historic payloads we have persisted
    private static async Task PrintTotalRows(NpgsqlConnection conn)
    {
        const string countSql = "SELECT COUNT(*) FROM raw_api_data;";
        await using var cmd = new NpgsqlCommand(countSql, conn);
        var total = await cmd.ExecuteScalarAsync();
        Console.WriteLine($"Total records in raw_api_data: {total}");
    }

    // Creates the audit table and ensures new columns exist if the schema evolves
    private static async Task EnsureAuditTableExists(NpgsqlConnection conn)
    {
        const string tableSql = @"
            CREATE TABLE IF NOT EXISTS api_fetch_audit (
                id SERIAL PRIMARY KEY,
                source_name TEXT NOT NULL,
                status_code TEXT NOT NULL,
                was_success BOOLEAN NOT NULL,
                posted_from TEXT NOT NULL,
                posted_to TEXT NOT NULL,
                fetched_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );";

        // Run the CREATE TABLE statement inside its own using scope
        await using (var cmd = new NpgsqlCommand(tableSql, conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        const string addPostedFrom = "ALTER TABLE api_fetch_audit ADD COLUMN IF NOT EXISTS posted_from TEXT";
        const string addPostedTo = "ALTER TABLE api_fetch_audit ADD COLUMN IF NOT EXISTS posted_to TEXT";

        // Add new columns only if they are missing, so reruns stay safe
        await using (var cmd = new NpgsqlCommand(addPostedFrom, conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(addPostedTo, conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // Writes one row per API fetch attempt to the audit trail
    private static async Task LogFetchAudit(
        NpgsqlConnection conn,
        HttpStatusCode statusCode,
        bool success,
        string postedFrom,
        string postedTo)`
    {
        const string insertSql = @"
            INSERT INTO api_fetch_audit (source_name, status_code, was_success, posted_from, posted_to)
            VALUES (@source_name, @status_code, @was_success, @posted_from, @posted_to);";

        await using var cmd = new NpgsqlCommand(insertSql, conn);
        cmd.Parameters.AddWithValue("source_name", "SAM.gov");
        cmd.Parameters.AddWithValue("status_code", statusCode.ToString());
        cmd.Parameters.AddWithValue("was_success", success);
        cmd.Parameters.AddWithValue("posted_from", postedFrom);
        cmd.Parameters.AddWithValue("posted_to", postedTo);

        await cmd.ExecuteNonQueryAsync();
        Console.WriteLine("Audit log entry recorded.");
    }

    // Returns either user-specified posted dates or a default 7-day window
    private static (string postedFrom, string postedTo) GetPostedDateRange()
    {
        string? postedFromEnv = Environment.GetEnvironmentVariable("SAM_POSTED_FROM");
        string? postedToEnv = Environment.GetEnvironmentVariable("SAM_POSTED_TO");

        if (!string.IsNullOrWhiteSpace(postedFromEnv) && !string.IsNullOrWhiteSpace(postedToEnv))
        {
            return (postedFromEnv.Trim(), postedToEnv.Trim());
        }

        // No env vars were provided, so default to "last 7 days"
        DateTime postedTo = DateTime.UtcNow.Date;
        DateTime postedFrom = postedTo.AddDays(-7);

        const string format = "MM/dd/yyyy";
        return (
            postedFrom.ToString(format, CultureInfo.InvariantCulture),
            postedTo.ToString(format, CultureInfo.InvariantCulture));
    }

    // Encodes parameters and builds the final SAM.gov search URL
    private static string BuildSearchUrl(string apiKey, string postedFrom, string postedTo)
    {
        string escapedKey = Uri.EscapeDataString(apiKey);
        string escapedFrom = Uri.EscapeDataString(postedFrom);
        string escapedTo = Uri.EscapeDataString(postedTo);
        return $"{ApiBaseUrl}?api_key={escapedKey}&postedFrom={escapedFrom}&postedTo={escapedTo}&limit=25";
    }
}
