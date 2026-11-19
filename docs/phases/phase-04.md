<!-- Phase 04: original requirements are preserved verbatim below the divider (only markdown heading markers were added). -->
> **Phase 4 – Customers, Bookings and Agreements**
> **Status:** see `docs/project-state.md`
> **Depends on:** Phase 1, Phase 2, Phase 3 (pricing snapshot). Payment/accounting controls referenced here are delivered by Phase 6 and Phase 8 (see docs/decisions/spec-issues.md, items 1 and 2)
> **Read first:** `CLAUDE.md`, `docs/project-state.md`, then architecture.md, database-design.md (concurrency, retention). Also apply `docs/global-requirements.md` (authorization, audit, documents, financial precision, concurrency, retention, MVC/API standards, testing, definition of done).
> Dependencies above are derived from the specification's entity and workflow references; the original specification lists only the sequential order.

---

# PHASE 4 – Customers, Bookings and Agreements

### Objective

Implement the complete sales lifecycle.

## 4.1 Customer entities

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

## 4.2 Sales entities

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

## 4.3 Booking workflows

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

## 4.4 Cash sales

Support:

- Full payment at booking.
- Payment received in multiple transactions.
- Cash sale with outstanding balance only if authorized.
- Payment verification.
- Final allocation.

Cash sale must not bypass payment and accounting controls.

## 4.5 Cancellation and transfer

Support:

- Booking cancellation.
- Agreement cancellation.
- Property release.
- Customer transfer.
- Ownership percentage changes.
- Refund processing.

Transfers must preserve the original ownership and agreement history.

### MVC screens

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

### API

- GET /api/v1/customers
- POST /api/v1/customers
- GET /api/v1/customers/{id}
- PUT /api/v1/customers/{id}
- POST /api/v1/bookings
- GET /api/v1/bookings/{id}
- POST /api/v1/bookings/{id}/approve
- POST /api/v1/bookings/{id}/cancel
- POST /api/v1/bookings/{id}/transfer

### Acceptance criteria

- Customer registration works.
- Multiple property ownership works.
- Joint ownership works.
- Booking conflicts are prevented.
- Cash sales are supported.
- Agreements are generated.
- Cancellation and transfer preserve history.
