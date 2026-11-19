# CLAUDE.md – Real Estate ERP

Single-company, multi-project real estate ERP (land, inventory, pricing, customers, bookings, installments, payments, expenses, government fees, double-entry accounting, reports, Flutter customer app). Entry point and phase index: `README.md`.

## Stack
ASP.NET Core 10 / C# 14 · MVC (Bootstrap 5) · Web API (`/api/v1`, Swagger) · EF Core Code First · MySQL 8.x · ASP.NET Identity · Serilog · Hangfire (Phase 11) · Flutter (Phase 10) · Docker.

## Architecture rules (fixed; see `docs/architecture.md`, `docs/decisions/architecture-decisions.md`)
- Clean Architecture, modular monolith, no microservices. Projects: `RealEstate.Domain/Application/Infrastructure/Web/Api` + test projects.
- Business logic only in Application/Domain services; MVC and API share them; controllers stay thin. Never expose Domain entities via API (DTOs/ViewModels).
- One company only (no multi-tenancy); isolation is by project assignment and customer ownership.

## Security (mandatory)
- Permission-based authorization enforced in backend services and API, plus project-level, data-level and customer-level access. Never trust client-supplied customer/project IDs.
- Files outside web root, authorized downloads. No secrets or credentials in code or git. Audit all sensitive changes (who/when/old/new/reason).

## Database and migrations (`docs/database-design.md`)
- EF Core migrations for every schema change; never edit applied migrations. DECIMAL(18,2) money, DECIMAL(18,4) rates, no floats.
- Explicit transactions + concurrency protection + idempotency for financial operations. Never physically delete posted financial records; use reversals/cancellations. Soft delete only where allowed. Server-side pagination.

## Conventions (`docs/coding-standards.md`)
Constructor DI, Problem Details errors, server-side validation, structured logging, no placeholder UI/inactive buttons.

## Testing
Every phase: domain, application, integration, API, database, authorization tests and UI verification. Build and tests must pass before a phase is called complete. Definition of done: `docs/global-requirements.md` Part 7.

## Working rules
- Work on ONE phase per session: read `docs/phases/phase-NN.md` and `docs/project-state.md` first; read only shared docs relevant to the task; do not read other phase specs.
- Inspect relevant existing code before modifying it; reuse existing components; do not explore the whole repo repeatedly.
- Implement only the approved scope (all of it, nothing from future phases). No unnecessary refactoring or rewriting of working code. Don't replan approved architecture.
- Requirements are authoritative; never simplify or drop them to save effort. If ambiguity materially affects accounting, ownership or financial correctness, ask one focused question; log choices in the decisions file. Known spec issues: `docs/decisions/spec-issues.md`.
- Run relevant build/tests once, fix what you broke, avoid redundant runs. Keep reports short.
- After finishing, update `docs/project-state.md` accurately (never mark Complete unless done-criteria are met).
- Git: follow `docs/git-workflow.md` (check status first, never overwrite others' changes, no secrets, no reset/rebase/force-push without authorization).
- Models: `docs/model-allocation.md` (user chooses the model; recommend escalation, don't assume you can switch).
- Prompts: `docs/prompts/phase-execution.md`, `docs/prompts/phase-review.md`.
