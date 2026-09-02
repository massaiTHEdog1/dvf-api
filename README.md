# DVF API

A REST API for querying the French **DVF (Données de valeurs foncières)** — the official
dataset of real estate transactions published by the French state on data.gouv.fr.
The data itself is not included in the repository and must be downloaded separately
— see [Data](#data).

The application is built with ASP.NET Core (net10.0) and stores the data in a local
SQLite database. At startup it creates the database, builds its indexes and imports
any DVF year that is not present yet.

## Projects

| Project    | Description                                                        |
|------------|--------------------------------------------------------------------|
| `dvf.api`  | The web application: SQLite import + the `POST /api/mutations` endpoint, Swagger UI, landing page, Serilog logging. |
| `dvf.tests`| xUnit tests (xUnit + `Microsoft.AspNetCore.Mvc.Testing`) covering the landing page and the mutations endpoint. |

## Data

The DVF files are **not included in the repository**. Download one zip per year
(`valeursfoncieres-<year>.txt.zip`, each about 70–90 MB) from the official dataset
on [data.gouv.fr](https://www.data.gouv.fr/fr/datasets/demandes-de-valeurs-foncieres/)
and put them in `dvf.api/mutations-zips/`.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- The DVF zip files in `dvf.api/mutations-zips/` (see [Data](#data))

## Getting started

```bash
# 1. Restore and build
dotnet build

# 2. Run (Development environment: http://localhost:5001)
cd dvf.api
dotnet run
```

On the first run the DVF zip files found in `dvf.api/mutations-zips/`
(`valeursfoncieres-<year>.txt.zip`) are imported into `dvf.api/database.db`.
This takes a few minutes; subsequent starts only import years that are new.

Then open:

- **Landing page** (French description, data license, legal notices): <http://localhost:5001/>
- **Swagger UI**: <http://localhost:5001/api/swagger>
- **OpenAPI document**: <http://localhost:5001/openapi/v1.json>

## The API

### `POST /api/mutations`

Returns rows from the `mutations` table with pagination.

Request body:

```jsonc
{
  "filters": [                                  // optional, combined with AND
    { "field": "Code postal", "operator": "EQUALS", "operand": "75001" },
    { "field": "Valeur fonciere", "operator": "GREATER_THAN", "operand": 300000 }
  ],
  "skip": 0,                                    // mandatory, >= 0
  "take": 10                                    // mandatory, 1..1000
}
```

- **Fields** are the exact column names of the `mutations` table (spaces kept,
  accents dropped), e.g. `"Code postal"`, `"Code commune"`, `"Valeur fonciere"`,
  `"Date mutation"`.
- **Operators** (case-insensitive): `EQUALS`, `NOT_EQUALS`, `LIKE`,
  `GREATER_THAN`, `GREATER_THAN_OR_EQUALS`, `LOWER_THAN`, `LOWER_THAN_OR_EQUALS`.
  Which operators are accepted depends on the column type: numeric columns take the
  comparison/range operators, text columns take `EQUALS`/`NOT_EQUALS`/`LIKE`
  (SQL patterns with `%` and `_` wildcards), and the date column `"Date mutation"`
  takes all operators (range operators compare chronologically; operands in
  `"DD/MM/YYYY"` or `"YYYY-MM-DD"` format).
- **Response**: the requested page of rows plus pagination metadata
  (`totalRows`, `page`, `pageSize`, `totalPages`).
- **Errors**: invalid requests (missing pagination, unknown field, operator not
  allowed for the column type, wrong operand type) return `400` with an
  `{"error": "..."}` body.

Example:

```bash
curl -X POST http://localhost:5001/api/mutations \
  -H "Content-Type: application/json" \
  -d '{ "filters": [{ "field": "Code postal", "operator": "EQUALS", "operand": "75001" }], "skip": 0, "take": 5 }'
```

## Configuration

Settings live in `dvf.api/appsettings.json` (overridden by `appsettings.Development.json`
in the Development environment) under the `Configuration` section:

| Key                | Meaning                                            | Development value   |
|--------------------|----------------------------------------------------|---------------------|
| `ZipsFolder`       | Folder containing the DVF zip files                | `mutations-zips`    |
| `DatabasePath`     | Path of the SQLite database                        | `database.db`       |
| `LogsFolder`       | Folder for the daily rolling log files (Serilog)   | `logs`              |

## Logging

Serilog writes to the console and to daily rolling files in `logs/`
(`log-yyyyMMdd.log`). Every log line of a request carries a request id, which is
taken from the incoming `X-Request-Id` header or generated (GUID) and returned in
the `X-Request-Id` response header.

## Tests

```bash
dotnet test
```

## License

- Source code: [GPL-3.0](LICENSE).
- The underlying DVF data is published by the French state under the
  [Open Licence (ETALAB)](https://www.etalab.gouv.fr/licence-ouverte-open-licence).
