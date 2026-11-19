# Real Estate ERP

Real Estate Management, Sales, Installment & Financial ERP. Version 1.0 – approved scope for development.

One company manages multiple property development projects (e.g. different cities): land, inventory, pricing, customers, bookings, customized installments, payments, expenses, government fees, double-entry accounting, reporting and a customer mobile app.

## Technology stack
Backend ASP.NET Core 10 / C# 14 · Web: ASP.NET Core MVC (Bootstrap 5) · API: ASP.NET Core Web API (`/api/v1`) · Database: MySQL 8.x (EF Core Code First) · Mobile: Flutter · Docker · Serilog · Hangfire.

## Architecture
Clean Architecture, Modular Monolith (no microservices). Projects: `RealEstate.Domain`, `.Application`, `.Infrastructure`, `.Web`, `.Api`, plus test projects and the Flutter app. MVC and API share application services. Details: [architecture](docs/architecture.md).

## Documentation index
| Document | Purpose |
|---|---|
| [CLAUDE.md](CLAUDE.md) | Permanent Claude Code instructions |
| [docs/architecture.md](docs/architecture.md) | Layers, dependency flow, auth architecture, constraints |
| [docs/database-design.md](docs/database-design.md) | Database conventions, entities by phase, integrity, migrations |
| [docs/coding-standards.md](docs/coding-standards.md) | Development standards |
| [docs/global-requirements.md](docs/global-requirements.md) | Cross-phase requirements: authorization, audit, documents, precision, concurrency, retention, MVC/API standards, testing, definition of done |
| [docs/decisions/architecture-decisions.md](docs/decisions/architecture-decisions.md) | Approved business decisions and architecture rules |
| [docs/decisions/spec-issues.md](docs/decisions/spec-issues.md) | Specification ambiguities/conflicts (and original Parts 9–10) |
| [docs/project-state.md](docs/project-state.md) | Live progress tracker |
| [docs/model-allocation.md](docs/model-allocation.md) | Opus/Sonnet/Haiku guidance |
| [docs/git-workflow.md](docs/git-workflow.md) | Git rules |
| [docs/prompts/phase-execution.md](docs/prompts/phase-execution.md) | Reusable implementation prompt |
| [docs/prompts/phase-review.md](docs/prompts/phase-review.md) | Reusable phase verification prompt |

The original single-file specification is preserved in git history (commit `7f37aa2`, `README.md`).

## Development phases
Follow this order; do not skip or combine phases in a way that compromises testing and acceptance. Status is tracked in [project-state.md](docs/project-state.md) (all Not Started initially).

| # | Phase | Main outcome | Depends on | Spec |
|---|---|---|---|---|
| 0 | Technical Foundation | Solution, EF/MySQL, Identity, Serilog, Swagger, Docker, tests (gate: stable before continuing) | – | [phase-00](docs/phases/phase-00.md) |
| 1 | Company, Projects, Land and Access Control | Company, projects, land, users/roles/permissions | 0 | [phase-01](docs/phases/phase-01.md) |
| 2 | Property Inventory Management | Properties, blocks/sectors/streets, reservations | 0, 1 | [phase-02](docs/phases/phase-02.md) |
| 3 | Dynamic Property Pricing | Pricing engine, discounts, approvals | 1, 2 | [phase-03](docs/phases/phase-03.md) |
| 4 | Customers, Bookings and Agreements | Customers, bookings, agreements, cancellation/transfer | 1, 2, 3 | [phase-04](docs/phases/phase-04.md) |
| 5 | Installment and Payment Agreement Engine | Plans, schedule generation, amendments | 4 | [phase-05](docs/phases/phase-05.md) |
| 6 | Payments, Allocation and Customer Ledgers | Payments, allocation, receipts, ledgers, recovery | 4, 5 (accounting posting needs 8) | [phase-06](docs/phases/phase-06.md) |
| 7 | Expenses, Vendors and Government Fees | Expenses, vendor payables, government fees | 1 (accounting posting needs 8) | [phase-07](docs/phases/phase-07.md) |
| 8 | Complete Double-Entry Accounting | Chart of accounts, journals, posting rules, statements, bank reconciliation | 4–7 | [phase-08](docs/phases/phase-08.md) |
| 9 | Reports and Management Dashboards | Dashboards, reports, PDF/Excel export | 8 | [phase-09](docs/phases/phase-09.md) |
| 10 | Flutter Customer Application | Customer mobile app, payment-gateway readiness | 4, 5, 6 | [phase-10](docs/phases/phase-10.md) |
| 11 | Notifications, Automation and GIS Foundation | Notifications, Hangfire jobs, GIS entities | 5, 6, 10 | [phase-11](docs/phases/phase-11.md) |

Dependencies are derived from the spec's references (see [spec-issues](docs/decisions/spec-issues.md)).

## Workflow
One phase per Claude Code session: paste [phase-execution.md](docs/prompts/phase-execution.md) with the phase number, then verify in a fresh session with [phase-review.md](docs/prompts/phase-review.md).
