<!-- Phase 06: original requirements are preserved verbatim below the divider (only markdown heading markers were added). -->
> **Phase 6 – Payments, Allocation and Customer Ledgers**
> **Status:** see `docs/project-state.md`
> **Depends on:** Phase 4, Phase 5. Step 9 'Accounting posting' depends on Phase 8 (see docs/decisions/spec-issues.md, item 1)
> **Read first:** `CLAUDE.md`, `docs/project-state.md`, then architecture.md, database-design.md (transactions, concurrency, retention). Also apply `docs/global-requirements.md` (authorization, audit, documents, financial precision, concurrency, retention, MVC/API standards, testing, definition of done).
> Dependencies above are derived from the specification's entity and workflow references; the original specification lists only the sequential order.

---

# PHASE 6 – Payments, Allocation and Customer Ledgers

### Objective

Build a reliable payment collection and customer account system.

## 6.1 Entities

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

## 6.2 Payment methods

- Cash.
- Bank transfer.
- Cheque.
- Online transfer.
- Card.
- Payment gateway.

## 6.3 Payment workflow

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

## 6.4 Payment allocation

Support:

- Oldest-due-first allocation.
- Manual allocation.
- Specific installment allocation.
- Charge-specific allocation.
- Advance balance.
- Unallocated balance.

Prevent over-allocation.

Use database transactions and concurrency protection.

## 6.5 Customer ledger

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

## 6.6 Receipts

Generate:

- Unique receipt numbers.
- PDF receipts.
- Duplicate copies.
- Payment references.
- Verification information.
- Reversal history.

## 6.7 Recovery

- Upcoming dues.
- Overdue dues.
- Recovery assignments.
- Follow-up records.
- Defaulter reports.
- Recovery officer performance.

### MVC screens

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

### API

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

### Acceptance criteria

- Payments are recorded accurately.
- Allocations cannot exceed received payments.
- Customer ledgers reconcile.
- Receipts are generated.
- Refunds work.
- Duplicate payment submissions cannot create duplicate financial entries.
- Recovery reports are accurate.
