# Phase Review Prompt

Use in a fresh session, ideally after the implementation session is committed. (Recommended model: Sonnet; use Opus for Phases 6 and 8 or for security audits.)

---

PHASE: 03
PHASE_DOC: docs/phases/phase-{PHASE}.md

Independently verify whether this phase is actually complete. **Review only: do not modify code, migrations or tests, and do not fix anything.** The only file you may change is the review result in `docs/project-state.md` (status/notes), and only if I ask.

1. Read `CLAUDE.md`, `PHASE_DOC`, `docs/project-state.md`, `docs/global-requirements.md`, and the relevant parts of `docs/architecture.md` / `docs/database-design.md`. Do not trust project-state.md claims; verify them.
2. Inspect the code, migrations and tests for this phase (scope it using the files and migrations listed for this phase in project-state.md, and flag any in-scope file missing from that list or any change outside the listed scope you can detect).
3. Build and run the tests yourself; report actual output.
4. Check, item by item, with file/line evidence:
   - Every functional requirement, entity, screen and endpoint in the phase document.
   - Every acceptance criterion (met / partially met / unmet).
   - Architectural consistency (layering, no business logic in controllers, shared services, DTO/ViewModel use, no domain entities in API).
   - Database integrity (constraints, migrations apply cleanly, history/audit tables, precision, no physical deletes of posted data).
   - Authorization (permission, project and customer-level checks in backend; authorization tests).
   - Error handling and validation.
   - Concurrency, transactions and idempotency where the phase requires them.
   - Tests (coverage of required categories, failures, duplicates, concurrency) and build status.
   - Regression risks to earlier phases, and scope creep into later phases.
5. Report:
   - **Verdict:** Complete / Not complete (do not say Complete if any important acceptance criterion is unmet or unverified).
   - **Critical defects** (blocking: wrong behavior, security, data integrity, failing tests/build, unmet acceptance criteria).
   - **Recommendations** (non-blocking improvements), clearly separate from critical defects.
   - Criteria checklist and test/build results.
   Keep it concise. Ask me before fixing anything.
