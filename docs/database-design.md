# Database Design

Sources: `README.md` history (original spec Parts 1, 2.2, 4.4-4.6) and the phase documents. Entity lists live in the phase documents; this file holds only cross-cutting conventions. No schema beyond the specification is defined here; detailed ERDs are produced per phase.

## Technology and boundaries
- MySQL 8.x, EF Core Code First, migrations for every schema change. (Specific EF provider is not specified; see `decisions/spec-issues.md`.)
- Single company. No multi-company tenancy. Isolation is by **project**: users access only assigned projects (`UserProject`, Phase 0/1); customers access only their own records (derived from account relationships).
- Each project has independent inventory, sales, expenses, agreements and project-level reporting; company-wide accounting consolidates all projects.

## Entities by phase (names only; fields in phase docs)
| Phase | Main entities |
|---|---|
| 0 | Company, ApplicationUser, Role, Permission, UserRole, RolePermission, UserProject, AuditLog, ApplicationSetting |
| 1 | Project (+Address, Document, ManagerAssignment, AreaHistory, StatusHistory), LandParcel, LandAcquisition, LandAdjustment, LandDistribution(+History), LandDocument |
| 2 | PropertyType, PropertyCategory, ProjectBlock, ProjectSector, ProjectStreet, Property, PropertyDimension, PropertyFeature, PropertyDocument, PropertyStatusHistory |
| 3 | PricingRule(+Condition), PropertyPrice(+History, Adjustment), DiscountRule, PricingApproval |
| 4 | Customer family, Booking family (incl. BookingPricingSnapshot), CustomerAgreement, AgreementVersion/Document/Amendment |
| 5 | InstallmentPlan(+Component), AgreementCharge, PaymentSchedule(+Item), ScheduleAmendment, LateFeeRule, Waiver, ChargeType |
| 6 | Payment family, Refund(+Approval), Customer/AgreementLedgerEntry, RecoveryAssignment, CustomerFollowUp |
| 7 | Expense family, Vendor, VendorInvoice/Payment, GovernmentFee* |
| 8 | ChartOfAccount, AccountGroup, FiscalYear, AccountingPeriod(+Lock), JournalEntry(+Line), GeneralLedgerEntry, AccountingVoucher, PostingRule, Bank/Cash accounts, BankTransaction, BankReconciliation, CostCenter, ProjectCostAllocation |
| 10-11 | GatewayTransaction, PaymentWebhookLog; Notification family; GIS foundation entities |

Key relationships follow the hierarchy Company → Project → Block/Sector → Street → Property → Booking → Agreement → Schedule → Payment → Ledger/Accounting. Immutable snapshots: `BookingPricingSnapshot`; history tables for land area, property status, price, agreement versions, schedule amendments.

## Data integrity
- DB constraints (FK, unique, check where applicable) plus optimistic concurrency on: property bookings, payment posting, payment allocation, journal posting, agreement amendments. Use unique constraints and idempotency keys to stop duplicate payments.
- Explicit transactions for all financial operations. Posted payments, journals, confirmed agreements, approved booking history, allocations and reversal history are never physically deleted (reversal/cancellation workflows only). Soft delete only where business rules allow.
- Money: DECIMAL(18,2); unit rates DECIMAL(18,4); no floating point; configurable rounding.
- Audit: creator, modifier, approver, canceller, previous/new values, timestamp, reason (`AuditLog` plus entity-level history).
- Every posted journal balances (debit = credit); closed accounting periods reject unauthorized postings.
- Property-specific: no double booking; ownership percentages ≤ 100%; schedules reconcile to agreement charge total; land distribution reconciles to project area.

## Migration conventions
- One migration per logical change, descriptive names (e.g. `Phase01_AddLandManagement`), generated from the Infrastructure project; never edit an applied migration; review generated SQL for destructive operations; migrations must run cleanly on an empty database and on the previous state.
- Entity configuration lives in `Infrastructure/EntityConfigurations` (Fluent API), not attributes on domain entities.
- No source seed data containing real credentials; initial administrator created securely (Phase 0).
(The naming/location conventions above are working conventions derived from the specified folder structure; adjust via the decisions log.)

## Indexing and performance
- Server-side pagination, filtering and sorting for all large lists. Index foreign keys and the columns used for search/filter (project, block, status, customer, due date, payment reference, document/voucher numbers). Add indexes with the phase that introduces the query; justify in the phase's migration.
- Reports (Phase 9) read from actual business data / accounting engine; avoid unbounded queries.
