# Coding Standards

Derived from the specification (Parts 2.2, 4-7). The specification does not define naming/style beyond what is below; follow the .NET conventions and match existing code once it exists.

## Naming and organization
- Projects: `RealEstate.{Domain|Application|Infrastructure|Web|Api}`; Application organised by module folder (Projects, LandManagement, Inventory, Pricing, Customers, Sales, Agreements, Installments, Payments, Recovery, Expenses, GovernmentFees, Accounting, Banking, Vendors, Reporting, Documents, Notifications).
- PascalCase types/members, camelCase locals, `I` prefix for interfaces, `*Service` for application/domain services (e.g. `PricingCalculationService`, `ScheduleGenerationService`), DTOs for API, ViewModels for MVC. Entity names exactly as listed in phase documents. Routes: `/api/v1/` RESTful plural nouns.
- Feature code lives in its module folder in every layer; do not create cross-module shortcuts through Infrastructure.

## Dependency injection and layering
- Constructor injection; register per layer via extension methods. Application defines abstractions, Infrastructure implements them.
- Business operations in application services; controllers only bind, authorize, call, map. MVC and API call the same services. Reuse existing services/components before creating new ones.

## Error handling and validation
- Global exception handling; API errors as Problem Details with a consistent shape. Validate requests (server-side always; client-side as convenience). Domain invariants enforced in the domain/application layer, not only in UI.
- Business rule violations return clear, non-leaking messages; unexpected errors are logged, not exposed.

## Logging
- Serilog, structured logging; never log secrets, tokens, full card/payment data or personal identifiers unnecessarily. Audit trail (who/when/old/new/reason) is separate from logging.

## Security
- Permission checks on every service entry point and endpoint; project and customer-level data access enforced in backend. Never trust client-supplied customer/project IDs. Files stored outside web root with authorized download only. No secrets in source or committed config; use environment settings/user secrets. Never expose domain entities via API.

## Money and data
- DECIMAL(18,2)/(18,4), no floats, central rounding policy, currency-specific formatting. Explicit transactions, concurrency tokens and idempotency for financial writes. No physical deletion of posted financial data.

## UI (MVC)
- Bootstrap 5, responsive; list/create/edit/details with search, filtering, pagination, validation, permission checks; confirmation dialogs, toasts, loading and empty states. Desktop: multi-column (three fields per row when suitable); mobile: one field per row, full-width controls, responsive tables or cards. No inactive buttons or placeholder functionality. Full list: `docs/global-requirements.md` Part 5.

## Testing
- Each phase: domain unit, application service, integration, API, database, authorization tests, and UI verification. Test failures, duplicates, concurrency, refunds, cancellation, amendments. Deterministic engines (pricing, schedule generation, allocation) need thorough unit tests. Reuse shared test fixtures/base classes. Definition of done: `docs/global-requirements.md` Part 7.

## Reusable components
- Shared kernel in Domain/Application for: audit, pagination, result/error types, permission constants, file storage abstraction, document service, numbering (receipts, vouchers, codes), money/rounding helpers. Check for an existing one before adding.
