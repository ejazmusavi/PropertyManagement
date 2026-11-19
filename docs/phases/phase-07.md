<!-- Phase 07: original requirements are preserved verbatim below the divider (only markdown heading markers were added). -->
> **Phase 7 – Expenses, Vendors and Government Fees**
> **Status:** see `docs/project-state.md`
> **Depends on:** Phase 1 (projects). 'Accounting posting' depends on Phase 8 (see docs/decisions/spec-issues.md, item 1)
> **Read first:** `CLAUDE.md`, `docs/project-state.md`, then architecture.md, database-design.md. Also apply `docs/global-requirements.md` (authorization, audit, documents, financial precision, concurrency, retention, MVC/API standards, testing, definition of done).
> Dependencies above are derived from the specification's entity and workflow references; the original specification lists only the sequential order.

---

# PHASE 7 – Expenses, Vendors and Government Fees

### Objective

Manage every company expense and payable obligation.

## 7.1 Expense entities

- Expense
- ExpenseCategory
- Vendor
- VendorInvoice
- VendorPayment
- ExpenseApproval
- ExpenseAttachment

## 7.2 Expense categories

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

## 7.3 Expense workflow

1. Expense creation.
2. Category selection.
3. Project assignment.
4. Supporting document upload.
5. Approval.
6. Payment.
7. Accounting posting.

Support unpaid and partially paid expenses.

## 7.4 Government fee management

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

### MVC screens

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

### API

- GET /api/v1/expenses
- POST /api/v1/expenses
- POST /api/v1/expenses/{id}/approve
- POST /api/v1/expenses/{id}/pay
- GET /api/v1/vendors
- POST /api/v1/vendors
- GET /api/v1/government-fees
- POST /api/v1/government-fees/assessments

### Acceptance criteria

- Expenses are recorded by project.
- Vendor payables are tracked.
- Government fee obligations are separated.
- Approval workflows work.
- Accounting entries are generated correctly.
