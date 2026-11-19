REAL ESTATE ERP

Master Software Requirements & Phase-Wise Implementation Specification

Project: Real Estate Management, Sales, Installment & Financial ERP
Version: 1.0
Status: Approved scope for development
Backend: ASP.NET Core 10 / C# 14
Web: ASP.NET Core MVC
Database: MySQL 8.x
API: ASP.NET Core Web API
Mobile: Flutter
Architecture: Clean Architecture / Modular Monolith

---

PART 1 – APPROVED BUSINESS DECISIONS

The following decisions are confirmed and must be treated as fixed architectural requirements.

Decision| Approved requirement
Company structure| One company managing multiple projects
Accounting| Complete double-entry accounting
Project area| Total area and detailed land distribution
Property types| Plots, houses, apartments, shops and commercial units
Payments| Office collection and future online payment integration

1.1 Business hierarchy

The application follows this hierarchy:

Company
→ Projects
→ Land and project planning
→ Blocks / Sectors
→ Streets
→ Properties
→ Customers
→ Bookings
→ Agreements
→ Installments
→ Payments
→ Accounting

A single company may operate multiple projects in different cities.

Example:

- Serene Rawalpindi – 200 Kanals.
- Serene Skardu – 30 Kanals.
- Future projects – unlimited.

Each project has independent inventory, sales, expenses, customer agreements and project-level financial reporting.

Company-wide accounting consolidates financial information from all projects.

1.2 Important accounting assumption

Implement full double-entry accrual accounting.

However, do not assume that every booking represents immediately recognized accounting revenue.

The accounting treatment of property sales, advances, customer receivables, development charges, taxes, land acquisition and construction costs must be configurable through approved posting rules.

Support both:

- Contractual customer account balances.
- Actual accounting balances.

These are related but must not be treated as interchangeable.

---

PART 2 – SOLUTION ARCHITECTURE

2.1 Solution projects

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

2.2 Architecture rules

1. Use Clean Architecture.
2. Use a Modular Monolith.
3. Do not introduce microservices.
4. Use EF Core Code First.
5. Use MySQL.
6. Use Application services for business operations.
7. MVC and API must share application services.
8. Do not duplicate business logic in controllers.
9. Use explicit database transactions for financial operations.
10. Use permission-based authorization.
11. Maintain audit trails.
12. Use soft deletion only where business rules allow it.
13. Never physically delete posted financial transactions.
14. Use database constraints and concurrency protection.
15. Use server-side pagination for large datasets.

---

PART 3 – PHASE-WISE IMPLEMENTATION

PHASE 0 – Technical Foundation

Objective

Create a stable, maintainable development environment.

Implementation tasks

- Inspect existing repository.
- Verify .NET and Flutter SDKs.
- Verify MySQL availability.
- Create solution and projects.
- Configure project references.
- Configure dependency injection.
- Configure EF Core and MySQL.
- Configure Identity.
- Configure Serilog.
- Configure Swagger.
- Configure global exception handling.
- Configure validation.
- Configure environment settings.
- Configure Docker.
- Configure automated tests.
- Configure database migrations.

Database entities

Initial:

- Company
- ApplicationUser
- Role
- Permission
- UserRole
- RolePermission
- UserProject
- AuditLog
- ApplicationSetting

MVC screens

- Login.
- Dashboard shell.
- Navigation layout.
- Profile.
- Change password.
- User management shell.

API endpoints

- POST /api/v1/auth/login
- POST /api/v1/auth/refresh
- POST /api/v1/auth/logout
- GET /api/v1/auth/me
- GET /api/v1/health

Acceptance criteria

- Solution builds successfully.
- Database connection works.
- Migrations execute successfully.
- Authentication works.
- API documentation works.
- Initial administrator can be created securely.
- Tests run successfully.
- Docker development environment starts.

Phase gate: No further development until the foundation is stable.

---

PHASE 1 – Company, Projects, Land and Access Control

Objective

Implement the company's organizational structure and complete project land management.

1.1 Company management

Features:

- Company profile.
- Company logo.
- Contact information.
- Address.
- Currency settings.
- Fiscal year.
- Timezone.
- Financial settings.
- Document settings.

One company only. Do not introduce multi-company tenancy.

1.2 Project management

Entities:

- Project
- ProjectAddress
- ProjectDocument
- ProjectManagerAssignment
- ProjectAreaHistory
- ProjectStatusHistory

Project fields:

- ProjectCode
- ProjectName
- Description
- City
- Address
- StartDate
- ExpectedCompletionDate
- InitialArea
- CurrentArea
- MeasurementUnit
- Status
- ProjectManagerId

Project status:

- Planning
- Active
- OnHold
- Completed
- Archived

1.3 Land management

Entities:

- LandParcel
- LandAcquisition
- LandAdjustment
- LandDistribution
- LandDistributionHistory
- LandDocument

Track:

- Original land.
- Acquired land.
- Added land.
- Reduced land.
- Total project area.
- Saleable area.
- Roads.
- Parks.
- Open spaces.
- Commercial areas.
- Residential areas.
- Reserved areas.
- Other facilities.

Every land adjustment must contain:

- Date.
- Previous area.
- Adjustment.
- New area.
- Reason.
- Supporting documents.
- Approved by.

Do not allow arbitrary project area changes without a history record.

Land distributions must reconcile to the project land area, with any permitted unclassified area explicitly identified.

1.4 User management and permissions

Roles:

- Super Admin.
- Director.
- Project Manager.
- Sales Manager.
- Accountant.
- Cashier.
- Recovery Officer.
- Inventory Manager.
- Customer Support.

Support granular permissions and project assignments.

MVC screens

- Company profile.
- Project list.
- Create/edit project.
- Project details.
- Land management.
- Land distribution.
- Area adjustment history.
- Land documents.
- User management.
- Role management.
- Permission management.
- Project access assignment.

API

- GET /api/v1/projects
- POST /api/v1/projects
- GET /api/v1/projects/{id}
- PUT /api/v1/projects/{id}
- GET /api/v1/projects/{id}/land
- POST /api/v1/projects/{id}/land/acquisitions
- POST /api/v1/projects/{id}/land/adjustments
- GET /api/v1/projects/{id}/land/history
- GET /api/v1/users
- POST /api/v1/users
- PUT /api/v1/users/{id}
- GET /api/v1/roles
- GET /api/v1/permissions

Acceptance criteria

- Multiple projects can be managed.
- Project areas update through controlled transactions.
- Land distribution is tracked.
- Historical records are preserved.
- Project access restrictions work.
- Users cannot access unauthorized projects.

---

PHASE 2 – Property Inventory Management

Objective

Manage every individual property within each project.

2.1 Property classification

Support:

- Residential plots.
- Commercial plots.
- Houses.
- Apartments.
- Shops.
- Offices.
- Custom property categories.

2.2 Property organization

Entities:

- PropertyType
- PropertyCategory
- ProjectBlock
- ProjectSector
- ProjectStreet
- Property
- PropertyDimension
- PropertyFeature
- PropertyDocument
- PropertyStatusHistory

2.3 Property details

Each property must support:

- Unique property code.
- Project.
- Block.
- Sector.
- Street.
- Property number.
- Property type.
- Size.
- Measurement unit.
- Dimensions.
- Location description.
- Floor number, where applicable.
- Corner status.
- Main road facing.
- Park facing.
- Development status.
- Availability status.
- Documents.
- Remarks.

Property dimensions must be flexible because plots, apartments and houses have different physical characteristics.

2.4 Inventory workflows

Statuses:

- Available.
- Reserved.
- Booked.
- Sold.
- Allocated.
- PossessionReady.
- PossessionDelivered.
- Transferred.
- Cancelled.

Reservation must support:

- Reservation date.
- Expiry date.
- Reservation amount.
- Reserved for customer.
- Approved by.

Implement controlled state transitions.

2.5 Inventory reports

- Total inventory.
- Available properties.
- Reserved properties.
- Booked properties.
- Sold properties.
- Properties by project.
- Properties by block.
- Properties by type.
- Properties by size.
- Inventory history.

MVC screens

- Property dashboard.
- Property types.
- Blocks.
- Sectors.
- Streets.
- Property list.
- Create/edit property.
- Property details.
- Property documents.
- Property status history.
- Inventory reports.

API

- GET /api/v1/properties
- POST /api/v1/properties
- GET /api/v1/properties/{id}
- PUT /api/v1/properties/{id}
- GET /api/v1/properties/available
- POST /api/v1/properties/{id}/reserve
- POST /api/v1/properties/{id}/release-reservation
- GET /api/v1/projects/{id}/inventory-summary

Acceptance criteria

- All property categories are supported.
- Properties belong to valid projects.
- Property codes are unique within the appropriate business scope.
- Reservation expiry works.
- Double booking is prevented.
- Property history is retained.

---

PHASE 3 – Dynamic Property Pricing

Objective

Build a configurable pricing engine.

3.1 Pricing entities

- PricingRule
- PricingRuleCondition
- PropertyPrice
- PropertyPriceHistory
- PropertyPriceAdjustment
- DiscountRule
- PricingApproval

3.2 Pricing hierarchy

Support:

1. Project pricing.
2. Block pricing.
3. Street pricing.
4. Property category pricing.
5. Property-specific pricing.
6. Customer-negotiated pricing.

More specific pricing rules should override general rules according to an explicit precedence policy.

3.3 Pricing factors

- Size.
- Base rate.
- Location.
- Corner.
- Road facing.
- Park facing.
- Commercial category.
- Development status.
- Other configurable premiums.

3.4 Pricing calculation

Create a dedicated PricingCalculationService.

Return:

- Base price.
- Size-based price.
- Premiums.
- Discounts.
- Additional charges.
- Final price.
- Applied pricing rules.

Never recalculate historical bookings using current pricing rules.

A booking must store an immutable pricing snapshot.

3.5 Approvals

Support approval for:

- Price overrides.
- Customer-specific discounts.
- Special pricing.
- Pricing exceptions.

MVC screens

- Pricing rules.
- Rate cards.
- Property pricing.
- Price history.
- Discount configuration.
- Pricing approvals.
- Price calculator.

API

- GET /api/v1/pricing/rules
- POST /api/v1/pricing/rules
- POST /api/v1/pricing/calculate
- GET /api/v1/properties/{id}/price
- GET /api/v1/pricing/history
- POST /api/v1/pricing/approvals

Acceptance criteria

- Configurable pricing works.
- Customer-specific pricing works.
- Price calculations are reproducible.
- Existing agreements are unaffected by new pricing.
- Approval workflows are enforced.

---

PHASE 4 – Customers, Bookings and Agreements

Objective

Implement the complete sales lifecycle.

4.1 Customer entities

- Customer
- CustomerAddress
- CustomerContact
- CustomerDocument
- CustomerNominee
- CustomerRelationship
- CustomerOwnership

Customer fields:

- CustomerCode.
- FullName.
- CNIC/Passport.
- Mobile.
- Email.
- Address.
- CustomerStatus.

Support one customer owning multiple properties.

Support multiple customers jointly owning one property or agreement.

Define ownership percentages and ensure the total valid ownership allocation does not exceed 100%.

4.2 Sales entities

- Booking
- BookingCustomer
- BookingProperty
- BookingPricingSnapshot
- BookingCharge
- BookingApproval
- BookingStatusHistory
- CustomerAgreement
- AgreementVersion
- AgreementDocument
- AgreementAmendment

4.3 Booking workflows

1. Select project.
2. Select available property.
3. Select customer.
4. Select ownership structure.
5. Calculate price.
6. Apply discount.
7. Add applicable charges.
8. Select cash or installment payment.
9. Generate booking.
10. Submit for approval.
11. Confirm booking.
12. Generate agreement and receipt.

4.4 Cash sales

Support:

- Full payment at booking.
- Payment received in multiple transactions.
- Cash sale with outstanding balance only if authorized.
- Payment verification.
- Final allocation.

Cash sale must not bypass payment and accounting controls.

4.5 Cancellation and transfer

Support:

- Booking cancellation.
- Agreement cancellation.
- Property release.
- Customer transfer.
- Ownership percentage changes.
- Refund processing.

Transfers must preserve the original ownership and agreement history.

MVC screens

- Customer list.
- Customer registration.
- Customer details.
- Customer documents.
- Booking list.
- New booking wizard.
- Booking details.
- Agreement preview.
- Booking approvals.
- Booking cancellation.
- Property transfer.

API

- GET /api/v1/customers
- POST /api/v1/customers
- GET /api/v1/customers/{id}
- PUT /api/v1/customers/{id}
- POST /api/v1/bookings
- GET /api/v1/bookings/{id}
- POST /api/v1/bookings/{id}/approve
- POST /api/v1/bookings/{id}/cancel
- POST /api/v1/bookings/{id}/transfer

Acceptance criteria

- Customer registration works.
- Multiple property ownership works.
- Joint ownership works.
- Booking conflicts are prevented.
- Cash sales are supported.
- Agreements are generated.
- Cancellation and transfer preserve history.

---

PHASE 5 – Installment and Payment Agreement Engine

Objective

Create a flexible installment engine that can handle any reasonable customer agreement.

5.1 Entities

- InstallmentPlan
- InstallmentPlanComponent
- CustomerAgreement
- AgreementCharge
- AgreementVersion
- AgreementAmendment
- PaymentSchedule
- PaymentScheduleItem
- ScheduleAmendment
- LateFeeRule
- Waiver
- ChargeType

5.2 Plan types

- Standard project plan.
- Property-category plan.
- Property-specific plan.
- Customer-specific plan.
- Cash agreement.

5.3 Charge components

Support:

- Down payment.
- Confirmation payment.
- Monthly installment.
- Quarterly installment.
- Half-yearly installment.
- Annual installment.
- Possession payment.
- Development charges.
- Utility charges.
- Government fees.
- Custom charges.

5.4 Component configuration

Every charge must support:

- Amount.
- Frequency.
- Number of installments.
- First due date.
- Subsequent due dates.
- Grace period.
- Late fee rules.
- Waiver rules.
- Applicable charge category.
- Tax treatment.
- Remarks.

5.5 Schedule generation

Implement a deterministic ScheduleGenerationService.

Support:

- Monthly schedules.
- Quarterly schedules.
- Half-yearly schedules.
- Annual schedules.
- Custom dates.
- Fixed dates.
- One-time payments.
- Irregular schedules.

The engine must handle:

- Month-end dates.
- Leap years.
- Rounding.
- Partial installments.
- Custom schedules.
- Agreement reconciliation.

Every generated schedule must reconcile with the agreement charge total.

5.6 Agreement amendments

Support:

- Rescheduling.
- Installment deferment.
- Installment reduction.
- Additional installments.
- Due-date changes.
- Penalty changes.
- Waivers.
- Additional charges.

All changes require an amendment record and applicable approval.

Never destroy original schedule history.

MVC screens

- Installment plan templates.
- Plan component configuration.
- Customer agreement.
- Installment schedule builder.
- Schedule preview.
- Agreement amendment.
- Schedule history.
- Late fee configuration.
- Waiver approvals.

API

- GET /api/v1/installment-plans
- POST /api/v1/installment-plans
- POST /api/v1/agreements/{id}/generate-schedule
- GET /api/v1/agreements/{id}/schedule
- POST /api/v1/agreements/{id}/amendments
- POST /api/v1/agreements/{id}/amendments/{amendmentId}/approve
- GET /api/v1/agreements/{id}/history

Acceptance criteria

- Multiple installment frequencies work.
- Custom customer plans work.
- Schedule totals reconcile.
- Partial and advance payments can be represented.
- Amendments preserve historical versions.
- All calculations pass automated tests.

---

PHASE 6 – Payments, Allocation and Customer Ledgers

Objective

Build a reliable payment collection and customer account system.

6.1 Entities

- Payment
- PaymentMethod
- PaymentReceipt
- PaymentAllocation
- PaymentVerification
- PaymentReversal
- Refund
- RefundApproval
- CustomerLedgerEntry
- AgreementLedgerEntry
- RecoveryAssignment
- CustomerFollowUp

6.2 Payment methods

- Cash.
- Bank transfer.
- Cheque.
- Online transfer.
- Card.
- Payment gateway.

6.3 Payment workflow

1. Payment submission.
2. Validation.
3. Verification where required.
4. Payment approval.
5. Payment posting.
6. Payment allocation.
7. Receipt generation.
8. Ledger update.
9. Accounting posting.

Unverified customer-submitted proof must not automatically become a confirmed payment.

6.4 Payment allocation

Support:

- Oldest-due-first allocation.
- Manual allocation.
- Specific installment allocation.
- Charge-specific allocation.
- Advance balance.
- Unallocated balance.

Prevent over-allocation.

Use database transactions and concurrency protection.

6.5 Customer ledger

Maintain:

- Customer consolidated ledger.
- Agreement-specific ledger.
- Property-specific financial statement.

Display:

- Agreement value.
- Charges.
- Payments.
- Discounts.
- Penalties.
- Waivers.
- Refunds.
- Outstanding.
- Overdue.
- Advance balance.

Do not confuse a scheduled installment with an actual posted receivable.

6.6 Receipts

Generate:

- Unique receipt numbers.
- PDF receipts.
- Duplicate copies.
- Payment references.
- Verification information.
- Reversal history.

6.7 Recovery

- Upcoming dues.
- Overdue dues.
- Recovery assignments.
- Follow-up records.
- Defaulter reports.
- Recovery officer performance.

MVC screens

- Payment collection.
- Payment verification.
- Payment approval.
- Payment allocation.
- Receipt management.
- Customer ledger.
- Agreement ledger.
- Refund processing.
- Recovery dashboard.
- Overdue installments.
- Follow-up history.

API

- POST /api/v1/payments
- GET /api/v1/payments/{id}
- POST /api/v1/payments/{id}/verify
- POST /api/v1/payments/{id}/approve
- POST /api/v1/payments/{id}/allocate
- GET /api/v1/customers/{id}/ledger
- GET /api/v1/agreements/{id}/ledger
- POST /api/v1/refunds
- POST /api/v1/refunds/{id}/approve
- GET /api/v1/recovery/overdue

Acceptance criteria

- Payments are recorded accurately.
- Allocations cannot exceed received payments.
- Customer ledgers reconcile.
- Receipts are generated.
- Refunds work.
- Duplicate payment submissions cannot create duplicate financial entries.
- Recovery reports are accurate.

---

PHASE 7 – Expenses, Vendors and Government Fees

Objective

Manage every company expense and payable obligation.

7.1 Expense entities

- Expense
- ExpenseCategory
- Vendor
- VendorInvoice
- VendorPayment
- ExpenseApproval
- ExpenseAttachment

7.2 Expense categories

- Salaries.
- Office rent.
- Utilities.
- Marketing.
- Construction.
- Land acquisition.
- Legal expenses.
- Transportation.
- Maintenance.
- Agent commission.
- Miscellaneous.

7.3 Expense workflow

1. Expense creation.
2. Category selection.
3. Project assignment.
4. Supporting document upload.
5. Approval.
6. Payment.
7. Accounting posting.

Support unpaid and partially paid expenses.

7.4 Government fee management

Entities:

- GovernmentFeeType
- GovernmentFeeAssessment
- GovernmentFeeCollection
- GovernmentFeePayment

Support:

- Stamp duty.
- Registration.
- Transfer fees.
- Applicable taxes.
- NOC fees.
- Development charges.
- Other configurable fees.

Distinguish:

- Customer liability.
- Customer collection.
- Government payable.
- Actual payment.
- Company-borne expense.

MVC screens

- Expense dashboard.
- Expense categories.
- Expense registration.
- Expense approvals.
- Vendors.
- Vendor invoices.
- Vendor payments.
- Government fee configuration.
- Customer fee assessment.
- Government payments.

API

- GET /api/v1/expenses
- POST /api/v1/expenses
- POST /api/v1/expenses/{id}/approve
- POST /api/v1/expenses/{id}/pay
- GET /api/v1/vendors
- POST /api/v1/vendors
- GET /api/v1/government-fees
- POST /api/v1/government-fees/assessments

Acceptance criteria

- Expenses are recorded by project.
- Vendor payables are tracked.
- Government fee obligations are separated.
- Approval workflows work.
- Accounting entries are generated correctly.

---

PHASE 8 – Complete Double-Entry Accounting

Objective

Implement a proper accounting system integrated with every financial module.

This phase is mandatory before declaring the ERP financially complete.

8.1 Accounting entities

- ChartOfAccount
- AccountGroup
- FiscalYear
- AccountingPeriod
- JournalEntry
- JournalEntryLine
- GeneralLedgerEntry
- AccountingVoucher
- PostingRule
- BankAccount
- CashAccount
- BankTransaction
- BankReconciliation
- CostCenter
- ProjectCostAllocation
- AccountingPeriodLock

8.2 Chart of accounts

Implement a hierarchical chart of accounts.

Main groups:

Assets:

- Cash.
- Bank.
- Customer receivables.
- Land inventory.
- Property inventory.
- Fixed assets.
- Other assets.

Liabilities:

- Customer advances.
- Government payables.
- Vendor payables.
- Tax liabilities.
- Other liabilities.

Equity:

- Capital.
- Retained earnings.
- Reserves.

Revenue:

- Property sales revenue.
- Development-related revenue.
- Other income.

Expenses:

- Operating expenses.
- Construction expenses.
- Marketing.
- Salaries.
- Administrative expenses.
- Other expenses.

Actual account classifications and recognition rules must be configurable and approved by the company's accountant.

8.3 Journal entry engine

Every posted journal must balance:

Total Debit = Total Credit.

Implement:

- Manual journals.
- Automatic journals.
- Voucher numbering.
- Journal approval.
- Journal reversal.
- Posting references.
- Audit history.

Posted entries cannot be edited or deleted.

8.4 Financial integration

Define posting rules for:

- Customer advances.
- Recognized property sales.
- Customer receivables.
- Customer payments.
- Refunds.
- Customer discounts.
- Penalties.
- Development charges.
- Government collections.
- Government payments.
- Vendor invoices.
- Vendor payments.
- Salaries.
- Operating expenses.
- Land acquisition.
- Construction costs.
- Bank transfers.
- Cash transfers.

Do not invent accounting treatment for legally or commercially ambiguous transactions.

Create configurable posting rules and require approval of the accounting policy before production.

8.5 Project accounting

Every relevant transaction should carry:

- Project ID.
- Account ID.
- Cost center.
- Transaction reference.

Support project-wise financial statements.

8.6 Financial reports

- Trial balance.
- General ledger.
- Cashbook.
- Bankbook.
- Journal register.
- Balance sheet.
- Income statement.
- Cash flow statement.
- Accounts receivable.
- Accounts payable.
- Project profitability.
- Project expense statement.
- Customer control account reconciliation.

8.7 Bank reconciliation

Support:

- Bank statement import.
- Transaction matching.
- Unmatched transactions.
- Reconciliation.
- Reconciliation history.

MVC screens

- Chart of accounts.
- Account groups.
- Journal entries.
- Journal approvals.
- General ledger.
- Trial balance.
- Cashbook.
- Bankbook.
- Bank reconciliation.
- Financial years.
- Accounting periods.
- Period locking.
- Financial statements.

API

- GET /api/v1/accounting/accounts
- POST /api/v1/accounting/accounts
- POST /api/v1/accounting/journals
- POST /api/v1/accounting/journals/{id}/approve
- POST /api/v1/accounting/journals/{id}/reverse
- GET /api/v1/accounting/trial-balance
- GET /api/v1/accounting/general-ledger
- GET /api/v1/accounting/balance-sheet
- GET /api/v1/accounting/income-statement
- GET /api/v1/accounting/cash-flow
- GET /api/v1/accounting/bank-reconciliation

Acceptance criteria

- Every posted journal balances.
- Financial transactions generate correct accounting entries.
- Customer control accounts reconcile.
- Project accounts reconcile.
- Financial statements are accurate.
- Closed periods cannot receive unauthorized postings.
- Reversal transactions work.

---

PHASE 9 – Reports and Management Dashboards

Objective

Create consolidated management reporting.

9.1 Company dashboard

Display:

- Total projects.
- Total land area.
- Total saleable area.
- Total inventory.
- Available properties.
- Sold properties.
- Total booking value.
- Total collections.
- Customer receivables.
- Overdue amount.
- Cash balance.
- Bank balance.
- Expenses.
- Revenue.
- Profit/loss.

9.2 Project dashboard

Display:

- Project area.
- Land distribution.
- Property inventory.
- Sales.
- Bookings.
- Collections.
- Outstanding.
- Overdue installments.
- Project expenses.
- Project profitability.

9.3 Customer reports

- Customer statement.
- Customer ledger.
- Customer property list.
- Payment history.
- Installment history.
- Outstanding balance.
- Overdue report.

9.4 Sales reports

- Daily sales.
- Monthly sales.
- Project-wise sales.
- Block-wise sales.
- Sales by property category.
- Sales by salesperson.
- Agent commissions.
- Cancellation report.

9.5 Collection reports

- Daily collection.
- Monthly collection.
- Project collection.
- Payment method summary.
- Cashier collection.
- Bank collection.
- Advance payment report.

9.6 Financial reports

Use the accounting engine as the authoritative source for financial statements.

Implement PDF and Excel exports.

All reports must support:

- Date range.
- Project.
- Block.
- Customer.
- Transaction type.
- Payment method.
- Status.

Acceptance criteria

All dashboard figures must be calculated from actual business data and reconcile with underlying financial records.

---

PHASE 10 – Flutter Customer Application

Objective

Allow customers to access their property and financial information securely.

10.1 Flutter architecture

Use a feature-based architecture.

Separate:

- Presentation.
- ViewModels/state management.
- Repositories.
- API services.
- Models.
- Navigation.
- Authentication.

Use a consistent state-management approach across the application.

10.2 Mobile modules

Authentication:

- Login.
- Forgot password.
- Secure logout.
- Token refresh.

Dashboard:

- Total properties.
- Total agreement value.
- Paid amount.
- Outstanding amount.
- Upcoming dues.

My properties:

- Project.
- Property number.
- Size.
- Category.
- Booking status.
- Agreement details.

Installments:

- Payment schedule.
- Due dates.
- Paid installments.
- Outstanding installments.
- Overdue installments.

Payments:

- Payment history.
- Receipt downloads.
- Payment instructions.
- Payment proof submission.
- Online payment initiation when enabled.

Ledger:

- Customer statement.
- Agreement statement.
- Transaction history.
- Outstanding balance.

Other:

- Notifications.
- Announcements.
- Support requests.
- Profile management.

10.3 Customer security

Customers may only access their own authorized records.

The API must derive customer identity from authenticated account relationships.

Never trust customer IDs supplied by the mobile application.

Implement secure file download authorization.

10.4 Online payment integration readiness

Create a payment provider abstraction.

Suggested components:

- IPaymentGateway.
- PaymentInitiationService.
- PaymentCallbackHandler.
- PaymentVerificationService.
- GatewayTransaction.
- PaymentWebhookLog.

Support:

- Payment initiation.
- Callback verification.
- Signature verification.
- Idempotency.
- Reconciliation.
- Failure handling.
- Refund support where provider allows it.

Do not hardcode JazzCash, Easypaisa or bank-specific logic into the domain layer.

Actual providers will be integrated once their credentials, APIs and commercial terms are available.

Acceptance criteria

- Flutter application builds.
- Customer authentication works.
- Customer data is correctly isolated.
- Property and agreement information displays.
- Ledger and installment information reconcile with the backend.
- PDF receipts can be downloaded.
- Payment proof submission works.
- Gateway integration can be added without redesigning the payment module.

---

PHASE 11 – Notifications, Automation and GIS Foundation

Objective

Prepare advanced business automation and future mapping.

11.1 Notifications

Entities:

- Notification
- NotificationTemplate
- NotificationPreference
- NotificationDeliveryLog

Support:

- Upcoming installment reminders.
- Overdue reminders.
- Payment confirmations.
- Booking confirmations.
- Agreement amendments.
- Possession notifications.
- Company announcements.

Channels:

- In-app.
- Email.
- SMS.
- WhatsApp.
- Mobile push notifications.

Integrate channels progressively.

11.2 Background processing

Use Hangfire for:

- Scheduled reminders.
- Recurring reports.
- Payment reconciliation tasks.
- Notification dispatch.
- Scheduled maintenance.

Jobs must be idempotent and have retry policies.

11.3 GIS foundation

Do not implement complete mapping until the core ERP is stable.

Prepare:

- ProjectBoundary.
- PropertyBoundary.
- MapLayer.
- SpatialReferenceSystem.
- MapDocument.

Future technologies:

- OpenStreetMap.
- MapLibre.
- GeoJSON.
- MySQL Spatial.
- QGIS.

Future features:

- Project boundary.
- Plot polygons.
- Streets.
- Blocks.
- Exact dimensions.
- Property status visualization.
- Plot search.
- Map-based property selection.

Use verified survey data for exact boundaries.

---

PART 4 – GLOBAL FUNCTIONAL REQUIREMENTS

These requirements apply to all phases.

4.1 Authorization

Implement:

- Role-based access.
- Permission-based access.
- Project-based access.
- Data-level access.
- Customer-level access.

Enforce authorization in backend services and API endpoints.

4.2 Audit

Track:

- Who created a record.
- Who modified it.
- Who approved it.
- Who cancelled it.
- Previous values.
- New values.
- Date and time.
- Reason.

Sensitive changes must have explicit audit records.

4.3 Documents

Support:

- CNIC.
- Booking agreements.
- Payment receipts.
- Land documents.
- Property documents.
- Expense invoices.
- Government receipts.
- Other attachments.

Store files outside the public web root.

Use secure document access.

4.4 Financial precision

- DECIMAL(18,2) for monetary amounts.
- DECIMAL(18,4) for unit rates.
- No floating-point money calculations.
- Configurable rounding.
- Currency-specific formatting.

4.5 Concurrency

Protect:

- Property bookings.
- Payment posting.
- Payment allocations.
- Journal posting.
- Agreement amendments.

Use appropriate optimistic concurrency, database transactions, unique constraints and idempotency.

4.6 Data retention

Never silently delete:

- Posted payments.
- Posted journal entries.
- Confirmed agreements.
- Approved booking history.
- Payment allocations.
- Reversal history.

Use controlled reversals and cancellation workflows.

---

PART 5 – STANDARD MVC SCREEN REQUIREMENTS

Every major business module should include:

1. List page.
2. Create page.
3. Edit page.
4. Details page.
5. Search.
6. Filtering.
7. Pagination.
8. Validation.
9. Permission checks.
10. Audit history where relevant.

Use:

- Bootstrap 5.
- Responsive forms.
- Professional tables.
- Consistent navigation.
- Dashboard cards.
- Confirmation dialogs.
- Toast messages.
- Loading states.
- Empty states.

Desktop:

- Use multiple columns where appropriate.
- Three form fields per row when suitable.

Mobile:

- One field per row.
- Full-width controls.
- Responsive tables or card alternatives.

Do not create inactive buttons or placeholder functionality.

---

PART 6 – API STANDARDS

Use versioned endpoints:

/api/v1/

Standards:

- RESTful resource naming.
- Request and response DTOs.
- Validation.
- Pagination.
- Filtering.
- Sorting.
- Standard error responses.
- Authentication.
- Permission checks.
- API documentation.

Never expose Domain entities directly through APIs.

Use consistent error responses, preferably Problem Details.

Customer APIs must enforce ownership and project access.

---

PART 7 – TESTING AND ACCEPTANCE

Each phase must have:

- Domain unit tests.
- Application service tests.
- Integration tests.
- API tests.
- Database tests.
- Authorization tests.
- UI verification.

Critical end-to-end scenarios:

1. Create a project.
2. Add land.
3. Create blocks and properties.
4. Configure pricing.
5. Register a customer.
6. Book a property.
7. Generate a customized installment agreement.
8. Generate payment schedules.
9. Collect down payment.
10. Collect monthly installment.
11. Allocate payment.
12. Generate receipt.
13. Verify customer ledger.
14. Verify accounting entries.
15. Record project expense.
16. Generate project financial statement.
17. Access customer mobile application.
18. Verify customer statement.

Test failures, duplicate requests, concurrent operations, refunds, cancellation and amendments.

Definition of done

A phase is complete only when:

- Business functionality is implemented.
- Database changes are complete.
- Migrations work.
- MVC screens work.
- APIs work.
- Authorization works.
- Validation works.
- Tests pass.
- Documentation is updated.
- Build succeeds.

A generated model or empty screen does not qualify as completed functionality.

---

PART 8 – PROJECT DEVELOPMENT ORDER

Follow this order:

Priority| Phase| Main outcome
1| Phase 0| Technical foundation
2| Phase 1| Company, projects and land
3| Phase 2| Property inventory
4| Phase 3| Pricing engine
5| Phase 4| Customers and sales
6| Phase 5| Installment engine
7| Phase 6| Payments and customer ledgers
8| Phase 7| Expenses and government fees
9| Phase 8| Complete accounting
10| Phase 9| Financial reporting
11| Phase 10| Flutter application
12| Phase 11| Notifications and GIS foundation

Do not skip or combine phases in a way that compromises their testing and acceptance criteria.

Some infrastructure work may be prepared earlier when dependencies require it, but functional completion must follow this sequence.

---

PART 9 – DOCUMENTATION AND PROGRESS TRACKING

Maintain the following files:

docs/Architecture/architecture.md
docs/Architecture/technical-decisions.md
docs/Database/database-design.md
docs/Database/entity-relationships.md
docs/BusinessRules/business-rules.md
docs/BusinessRules/accounting-rules.md
docs/API/api-documentation.md
docs/Development/implementation-roadmap.md
docs/Development/progress.md
docs/Development/known-issues.md
docs/Deployment/deployment-guide.md
docs/Deployment/backup-restore.md

Maintain progress.md throughout development.

Every completed feature must be listed with its test status.

At the end of each Claude session report:

CURRENT PHASE:
COMPLETED FEATURES:
FILES CREATED/MODIFIED:
DATABASE CHANGES:
APIS IMPLEMENTED:
MVC SCREENS:
TEST RESULTS:
BUILD STATUS:
KNOWN ISSUES:
NEXT TASK:

---

PART 10 – FIRST IMPLEMENTATION TASK

Start with repository inspection.

Then complete Phase 0.

Before writing large amounts of code, prepare:

1. Architecture document.
2. Database entity relationship plan.
3. Initial module dependency map.
4. Technical decisions.
5. Phase 0 task breakdown.

Then implement Phase 0.

Do not stop after preparing documentation.

After Phase 0 is verified, move to Phase 1 and continue sequentially.

If a business requirement is ambiguous and materially affects accounting, property ownership or financial correctness, ask one focused question and document the decision.

For other technical choices, make reasonable decisions and document them.

Final objective: Deliver a complete commercial real estate ERP that can reliably manage multiple property development projects, land, inventory, pricing, customers, bookings, customized installments, payments, company expenses, government fees, double-entry accounting, financial reporting and customer mobile applications.

Build working software. Verify it. Document it. Continue until all approved phases are complete.