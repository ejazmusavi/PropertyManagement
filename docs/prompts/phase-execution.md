# Phase Execution Prompt

Copy everything below the line into a fresh Claude Code session. Change only the `PHASE` line. (Recommended model: Sonnet; see `docs/model-allocation.md`.)

---

PHASE: 03
PHASE_DOC: docs/phases/phase-{PHASE}.md

Implement the phase named above, completely and only that phase.

1. Read `CLAUDE.md`, `PHASE_DOC`, and `docs/project-state.md`. Confirm all dependency phases are Complete in project-state.md; if not, stop and tell me.
2. Read only the shared docs relevant to this phase (`docs/architecture.md`, `docs/database-design.md`, `docs/coding-standards.md`, `docs/global-requirements.md`; `docs/decisions/spec-issues.md` for issues tagged to this phase). Do not read other phase documents.
3. Run `git status`. Do not touch unrelated uncommitted changes (`docs/git-workflow.md`).
4. Inspect only the existing source relevant to this phase (solution layout, modules and services it will extend, existing infrastructure to reuse). Do not explore the whole repository.
5. Implement the full approved scope in the phase document: entities, migrations, application services, MVC screens, API endpoints, authorization, validation, audit, tests. Follow existing architecture and decisions; do not replan them. Reuse existing components. No future-phase functionality, no unrelated refactoring, no placeholder screens or inactive buttons.
6. If a requirement is ambiguous in a way that materially affects accounting, ownership or financial correctness, ask me one focused question; otherwise decide reasonably and note it under "Important decisions".
7. Build and run the relevant tests; fix problems you introduced. Run each command only as often as needed. Verify every acceptance criterion; report any you cannot meet instead of claiming completion.
8. Update `docs/project-state.md` with accurate results (status Complete only if the definition of done is met; otherwise In Progress/Blocked with reasons). Record the decisions log in `docs/decisions/architecture-decisions.md` if new decisions were made.
9. Commit per `docs/git-workflow.md` (no secrets; no force/reset/rebase).
10. Finish with a concise completion report in this format (short lines, no narration):

CURRENT PHASE:
COMPLETED FEATURES:
FILES CREATED/MODIFIED: (grouped by module, not every file)
DATABASE CHANGES:
APIS IMPLEMENTED:
MVC SCREENS:
TEST RESULTS:
BUILD STATUS:
ACCEPTANCE CRITERIA: (met / unmet)
KNOWN ISSUES:
NEXT TASK:
