# GovContractsApp

GovContractsApp is a simple .NET console application that connects to the SAM.gov API, pulls federal contract opportunity data in JSON format, and stores the raw response in a PostgreSQL database. It also logs each API request so every data pull is tracked for auditing and ETL purposes.

---

## Overview

This project acts as the **raw data ingestion layer** for a government contracts data warehouse.

At a high level, the app:

* Connects to the live SAM.gov Opportunities API
* Retrieves contract data in JSON format
* Stores the full raw JSON payload in PostgreSQL (JSONB column)
* Logs metadata about each request (status code, timestamps, date filters, success flag)

The goal is to maintain both:

* A raw data feed (data lake style storage)
* A clean audit trail for traceability and future ETL processing

---

## Requirements

Before running the application, ensure you have:

* .NET 10 SDK installed
* PostgreSQL running and accessible
* A valid SAM.gov API key

To verify .NET installation:

```bash
dotnet --list-runtimes
```

---

## Configuration

The application uses environment variables for configuration.

### Required

* `SAM_API_KEY` – Your SAM.gov API key
* `GOV_CONTRACTS_DB` – PostgreSQL connection string

### Optional

* `SAM_POSTED_FROM` – Start date filter (`yyyy-MM-dd`)
* `SAM_POSTED_TO` – End date filter (`yyyy-MM-dd`)

If no dates are provided, the application defaults to retrieving data from the last 7 days.

### Example (macOS/Linux)

```bash
export SAM_API_KEY="your_api_key_here"
export GOV_CONTRACTS_DB="Host=localhost;Port=5432;Username=postgres;Password=yourpassword;Database=gov_contracts_dw"
export SAM_POSTED_FROM="2026-02-15"
export SAM_POSTED_TO="2026-02-22"
```

---

## Running the Application

From the root project directory:

```bash
dotnet run --project "GovContractsApp/GovContractsApp.csproj"
```

---

## Database Tables

The application automatically ensures the following tables exist:

### `raw_api_data`

Stores:

* Full JSON API response (JSONB)
* HTTP status code
* Posted date range
* Insert timestamp

### `api_fetch_audit`

Stores:

* Source name
* Status code
* Posted date range
* Success flag
* Fetch timestamp

---

## What Happens During Execution

1. The app calls the SAM.gov Opportunities API.
2. It prints the HTTP response status and a preview of the JSON payload.
3. It inserts the full raw payload into `raw_api_data`.
4. It logs the request in `api_fetch_audit`.
5. It displays the total number of stored raw records.

---

## Purpose

This application represents the first stage of an ETL pipeline. It focuses strictly on reliable data ingestion and audit logging. Structured transformations and relational mapping occur in later phases of the project.
