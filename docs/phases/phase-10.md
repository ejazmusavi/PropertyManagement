<!-- Phase 10: original requirements are preserved verbatim below the divider (only markdown heading markers were added). -->
> **Phase 10 – Flutter Customer Application**
> **Status:** see `docs/project-state.md`
> **Depends on:** Phases 4, 5, 6 (backend data and APIs); Phase 9 receipts/statements as applicable
> **Read first:** `CLAUDE.md`, `docs/project-state.md`, then architecture.md (customer-level access). Also apply `docs/global-requirements.md` (authorization, audit, documents, financial precision, concurrency, retention, MVC/API standards, testing, definition of done).
> Dependencies above are derived from the specification's entity and workflow references; the original specification lists only the sequential order.

---

# PHASE 10 – Flutter Customer Application

### Objective

Allow customers to access their property and financial information securely.

## 10.1 Flutter architecture

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

## 10.2 Mobile modules

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

## 10.3 Customer security

Customers may only access their own authorized records.

The API must derive customer identity from authenticated account relationships.

Never trust customer IDs supplied by the mobile application.

Implement secure file download authorization.

## 10.4 Online payment integration readiness

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

### Acceptance criteria

- Flutter application builds.
- Customer authentication works.
- Customer data is correctly isolated.
- Property and agreement information displays.
- Ledger and installment information reconcile with the backend.
- PDF receipts can be downloaded.
- Payment proof submission works.
- Gateway integration can be added without redesigning the payment module.
