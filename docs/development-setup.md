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

## Document storage (Phase 1)
Uploaded files (company logo, project and land documents) are stored on disk **outside the web root** under `FileStorage:RootPath` (default `App_Data/storage` beside the application; Docker uses the `document-storage` volume mounted at `/var/realestate/storage`). Files are served only through authorized controller actions, never as static files. The API and the Web app must point `FileStorage:RootPath` at the same directory when both are run, and the directory must be included in backups together with the database.

## Customer app (Phase 10)
The Flutter customer app is in `mobile/customer_app` (Flutter 3.47 / Dart 3.13; verified with `flutter analyze`, `flutter test`, `flutter build apk --debug`).
```bash
cd mobile/customer_app
flutter pub get
flutter test
flutter run --dart-define=API_BASE_URL=http://10.0.2.2:5008    # Android emulator -> API on this machine (default)
flutter build apk --release --dart-define=API_BASE_URL=https://<your-api-host>   # release builds must use HTTPS
```
Customers sign in with a login that the office creates on the customer record (Customers > customer > App login; needs `portal.accounts.manage`). The login has the **Customer** role (only `portal.access`). `flutter run` on a device needs the API reachable from the phone (`API_BASE_URL`).

Forgot password: `POST /api/v1/auth/forgot-password`. Until the Phase 11 e-mail/SMS channels exist the reset code is only written to the API log when `PasswordReset:LogToken` is true (set in `appsettings.Development.json`; never enable it in production).

Online payments are **off** by default (`PaymentGateway:Enabled=false`). For development only, the `sandbox` provider adapter can be switched on:
```bash
dotnet user-secrets set "PaymentGateway:Enabled" "true" --project src/RealEstate.Api
dotnet user-secrets set "PaymentGateway:Sandbox:SigningSecret" "<random secret>" --project src/RealEstate.Api
```
Callbacks are posted to `POST /api/v1/payment-webhooks/sandbox` with header `X-Signature` = hex HMAC-SHA256 of the raw body (JSON `eventId, reference, order, status[succeeded|failed|cancelled|pending], amount, currency`). Real providers (JazzCash, Easypaisa, banks) are added as `IPaymentGateway` adapters in Infrastructure once credentials, APIs and terms are available; nothing else changes.
