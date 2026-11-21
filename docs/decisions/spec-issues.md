# Specification Issues, Ambiguities and Conflicts

Found while reorganizing the original specification. **No decision has been invented.** Resolve with the user (or per Part 10 rule: ask one focused question when it materially affects accounting, ownership or financial correctness), then record the outcome in `architecture-decisions.md`.

| # | Issue | Affected | Suggested handling |
|---|---|---|---|
| 1 | (Phase 4: no payments or postings are simulated; Phase 4 only records amounts due, see decisions log. Phase 6: user chose an accounting outbox - `AccountingPostingRequest` rows written with each posted payment, reversal and refund payout, consumed by Phase 8; see decisions log 2026-11-20.) Phase 7: the same outbox is used for expenses, vendor payments and government fees (see decisions log 2026-11-20, Phase 7). Phase 8: the outbox is consumed by the automatic posting engine (decisions log 2026-11-21); Phase 6/7 "accounting entries" are now verifiable end to end once rules are approved. Phases 6 and 7 require "Accounting posting" (and Phase 4 cash sales must "not bypass accounting controls") but accounting is Phase 8. | 4, 6, 7, 8 | Ask user: stub/outbox for postings until Phase 8, or reorder. Per Part 8, functional completion follows sequence; Phase 6/7 accounting acceptance can only be fully verified after Phase 8. |
| 2 | (Resolved: Phase 4 created them; Phase 5 extended CustomerAgreement and AgreementAmendment for schedules and approval-based amendments, see decisions log 2026-11-20.) `CustomerAgreement`, `AgreementVersion`, `AgreementAmendment` appear in both Phase 4 and Phase 5. | 4, 5 | Assumption to confirm: Phase 4 creates them, Phase 5 extends them. |
| 3 | Phase 0 creates Role, Permission, UserProject; Phase 1.4 defines roles/permissions/project assignment. | 0, 1 | Confirm Phase 0 = schema + auth, Phase 1 = roles list, seed and management UI. |
| 4 | (Resolved in Phase 10: provider + ChangeNotifier view models, feature-based layers, go_router; see decisions log 2026-11-21.) Phase 0 says verify Flutter SDK, but Flutter app is Phase 10. Mobile state-management library unspecified. | 0, 10 | Decide before Phase 10. |
| 5 | (Phase 10: in-app notifications, announcements and support only, no SMS/e-mail/push; see decisions log 2026-11-21. Channels, reminders and scheduling remain Phase 11.) Phase 10 includes mobile notifications; notification entities/channels are Phase 11. | 10, 11 | Confirm Phase 10 uses minimal/in-app approach. **Phase 11: channels, templates, preferences, reminders and scheduling are implemented (in-app, e-mail over SMTP; SMS, WhatsApp and push as adapter slots without a provider); see decisions log 2026-11-21.** |
| 6 | (Phase 2 part resolved: property code unique per project; Phase 4 cash-sale authorization resolved: permission `bookings.balance.authorize`, see decisions log.) "Precedence policy" (Phase 3), "permitted unclassified area" (Phase 1), "unique within the appropriate business scope" (property code, Phase 2), "authorized" cash sale with balance (Phase 4: which permission) are undefined. | 1-4 | Needs user decision when phase starts. |
| 7 | (Phase 8: implemented as configurable posting rules with maker-checker policy approval; nothing seeded; events without an approved rule wait as failed or are marked not posted with a reason. The company accountant must still configure and approve the actual policy before production. See decisions log 2026-11-21.) Accounting treatment/recognition rules and posting policy are deliberately unspecified; must be approved by the accountant. | 6-8 | Do not invent; build configurable posting rules. |
| 8 | Unspecified technology: EF Core MySQL provider, token format/refresh storage, currency/default timezone, Hangfire storage, PDF/Excel libraries, test frameworks. | 0, 6, 9, 11 | Record choices in decisions log (Part 10 allows reasonable technical decisions). |
| 9 | Part 9 proposes a docs layout (`docs/Architecture/...`, `progress.md`, `known-issues.md`, etc.) different from the structure requested later. | Docs | Superseded by this reorganization: `project-state.md` replaces progress.md; API docs via Swagger; deployment/backup docs to be created in the phase that needs them (Phase 0 Docker). Some Part 9 files (business-rules, accounting-rules, api-documentation, deployment-guide, backup-restore, known-issues) intentionally not created yet. |
| 10 | Part 10 says "Do not stop after preparing documentation… continue sequentially" and "ask one focused question". This conflicts with the current instruction to run one phase per session. | Workflow | One phase per session now governs; the question rule is kept. |
| 11 | Phase 11 has no acceptance criteria; Phase 9 has a single criterion. | 9, 11 | Use global definition of done; confirm with user before Phase 11. **Phase 11: started on the user's instruction while its dependencies 5, 6 and 10 were Needs Review; verified against the objective in the phase document, the global definition of done and the checks listed in project-state.** |
| 12 | Phases are numbered 0–11 (12 phases); files are `phase-00.md`…`phase-11.md` to match the spec's numbering. | All | None needed. |
| 13 | Customer-level "Block" filter in Phase 9 requires block data (Phase 2) – ok; but "agent commissions" (Phase 9.4) have no agent/salesperson entity defined earlier (Phase 7 has only an expense category "Agent commission"). | 4, 7, 9 | Confirm where salesperson/agent is modeled. **Phase 9 (2026-11-21):** implemented without a new entity - agent commissions report lists approved expenses of category AGENT_COMMISSION (vendor = agent); salesperson = booking creator. Still open: commission rates/agent registry if the business wants them. |

---

## Appendix – Original Parts 9 and 10 (verbatim, superseded where noted in issues 9 and 10)

# PART 9 – DOCUMENTATION AND PROGRESS TRACKING

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

# PART 10 – FIRST IMPLEMENTATION TASK

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
