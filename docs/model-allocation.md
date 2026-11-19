# Model Allocation Guidelines

Recommendations only. The user selects the model (e.g. `/model`); Claude must not assume it can switch models itself. If a task needs a stronger model than the active one, say so briefly and let the user decide. Never lower quality or skip acceptance criteria to save cost.

| Model | Use for |
|---|---|
| Opus | Architecture decisions, complex database design, hard cross-module problems, complex debugging, security/architecture audits, resolving conflicting requirements (e.g. Phase 8 accounting design, Phase 5 schedule engine design, concurrency in Phase 6) |
| Sonnet (default) | Implementation of CRUD/business modules, APIs, UI, validation and tests, standard debugging, documentation updates, phase reviews of ordinary phases |
| Haiku (if available) | Lightweight exploration, simple documentation, code summaries, straightforward explanations |

Escalate when: the same fix fails twice, requirements conflict, a change touches financial correctness/security/concurrency across modules, or an audit is requested.
