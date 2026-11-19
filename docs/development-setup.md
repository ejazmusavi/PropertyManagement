# Development Setup (Phase 0)

Prerequisites: .NET SDK 10, MySQL 8.4 (or Docker), optional Flutter (Phase 10).

## Local run (no Docker)
Secrets stay out of the repository; use user-secrets or environment variables.
```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;Port=3306;Database=realestate;User=<user>;Password=<pwd>" --project src/RealEstate.Web
dotnet user-secrets set "Jwt:SigningKey" "<32+ random characters>" --project src/RealEstate.Web
# repeat both for src/RealEstate.Api (API reads the same keys)
dotnet run --project src/RealEstate.Web -- migrate
dotnet run --project src/RealEstate.Web -- create-admin --email you@example.com --name "Your Name" --company "Company"   # prompts for password (or set REALESTATE_ADMIN_PASSWORD)
dotnet run --project src/RealEstate.Web      # http://localhost:5096
dotnet run --project src/RealEstate.Api      # http://localhost:5008, Swagger at /swagger
```
`Database:MigrateOnStartup=true` makes a host apply migrations on start (used by Docker).

## Migrations
```bash
dotnet ef migrations add <Phase01_Name> --project src/RealEstate.Infrastructure --startup-project src/RealEstate.Web --output-dir Persistence/Migrations
```
Never edit an applied migration. Design-time needs no live database.

## Tests
`dotnet test RealEstate.slnx`. Database-backed tests need a MySQL account that can create/drop databases:
```bash
dotnet user-secrets set TestMySql "Server=localhost;Port=3306;User=<user>;Password=<pwd>" --project tests/RealEstate.Infrastructure.Tests
```
(or env var `REALESTATE_TEST_MYSQL`). Without it those tests are reported as Skipped. Only `realestate_test_*` databases created by the tests are dropped.

## Docker
Copy `.env.example` to `.env`, fill values, then `docker compose up --build` (MySQL 3307, API 5008, Web 5080). Create the admin with the command shown at the top of `docker-compose.yml`.
