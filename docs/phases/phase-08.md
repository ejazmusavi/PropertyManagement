<!-- Phase 08: original requirements are preserved verbatim below the divider (only markdown heading markers were added). -->
> **Phase 8 – Complete Double-Entry Accounting**
> **Status:** see `docs/project-state.md`
> **Depends on:** Phases 4-7 (sources of financial transactions to post)
> **Read first:** `CLAUDE.md`, `docs/project-state.md`, then architecture.md, database-design.md (financial precision, retention). Also apply `docs/global-requirements.md` (authorization, audit, documents, financial precision, concurrency, retention, MVC/API standards, testing, definition of done).
> Dependencies above are derived from the specification's entity and workflow references; the original specification lists only the sequential order.

---

# PHASE 8 – Complete Double-Entry Accounting

### Objective

Implement a proper accounting system integrated with every financial module.

This phase is mandatory before declaring the ERP financially complete.

## 8.1 Accounting entities

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

## 8.2 Chart of accounts

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

## 8.3 Journal entry engine

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

## 8.4 Financial integration

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

## 8.5 Project accounting

Every relevant transaction should carry:

- Project ID.
- Account ID.
- Cost center.
- Transaction reference.

Support project-wise financial statements.

## 8.6 Financial reports

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

## 8.7 Bank reconciliation

Support:

- Bank statement import.
- Transaction matching.
- Unmatched transactions.
- Reconciliation.
- Reconciliation history.

### MVC screens

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

### API

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

### Acceptance criteria

- Every posted journal balances.
- Financial transactions generate correct accounting entries.
- Customer control accounts reconcile.
- Project accounts reconcile.
- Financial statements are accurate.
- Closed periods cannot receive unauthorized postings.
- Reversal transactions work.
