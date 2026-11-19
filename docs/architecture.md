# Architecture

Clean Architecture, Modular Monolith. Fixed rules: `docs/decisions/architecture-decisions.md`. Business hierarchy: Company → Projects → Land and project planning → Blocks/Sectors → Streets → Properties → Customers → Bookings → Agreements → Installments → Payments → Accounting.

## Layers and dependency flow

- `RealEstate.Web` (MVC) and `RealEstate.Api` (Web API) → `RealEstate.Application` → `RealEstate.Domain`.
- `RealEstate.Infrastructure` implements Application abstractions (persistence, identity, file storage, background jobs, payment providers, notifications, logging); it depends on Application and Domain.
- Domain depends on nothing. Application must not reference Infrastructure, Web or Api.
- MVC and API are thin adapters over the same Application services; no business logic in controllers. Controllers/pages use ViewModels (Web) or DTOs (Api); Domain entities are never exposed through the API.
- Communication between layers: controller → application service (request model/DTO) → domain entities/services → repository/DbContext abstraction → MySQL. Financial operations run in explicit database transactions inside application services.

## Authentication and authorization

- ASP.NET Core Identity; API uses token authentication (login, refresh, logout, me endpoints in Phase 0); MVC has its own login screen (Phase 0).
- Role-based, permission-based, project-based, data-level and customer-level access, enforced in backend services and API endpoints (not only in UI). Customer identity is derived from authenticated account relationships, never from client-supplied IDs. See `docs/global-requirements.md` §4.1.

## Constraints

Single company (no multi-company tenancy); no microservices; EF Core Code First with MySQL; audit trails; soft delete only where business rules allow; posted financial records never physically deleted; server-side pagination; DB constraints and concurrency protection; no provider-specific payment logic in the domain layer.

## Specification of solution projects (verbatim, original section 2.1)

## 2.1 Solution projects

Create the following projects:

RealEstate.Domain

- Entities
- ValueObjects
- Enums
- DomainServices
- DomainEvents
- BusinessRules

RealEstate.Application

- Projects
- LandManagement
- Inventory
- Pricing
- Customers
- Sales
- Agreements
- Installments
- Payments
- Recovery
- Expenses
- GovernmentFees
- Accounting
- Banking
- Vendors
- Reporting
- Documents
- Notifications

RealEstate.Infrastructure

- Persistence
- EntityConfigurations
- Identity
- FileStorage
- BackgroundJobs
- PaymentProviders
- Notifications
- Logging

RealEstate.Web

- MVC Controllers
- Areas
- Views
- ViewModels
- Filters
- Web Authorization

RealEstate.Api

- API Controllers
- DTOs
- Authentication
- Authorization
- Middleware
- Swagger

Separate test projects and Flutter application.
