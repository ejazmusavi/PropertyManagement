# Change Safety (no git)

This project is developed locally without version control, so there is no undo. Follow these rules.

**User (before each phase):** make a dated copy or zip of the whole project folder (e.g. `RealEstate_before_phase-03_2026-11-19`). Keep it outside the project folder. This is the real safety net.

**Claude:**
1. Before editing, confirm the user has made the backup; if unsure, ask once.
2. Change only files within the current phase's scope. Never delete, overwrite, move or rename files outside that scope without asking first. Do not touch user changes you did not make.
3. Never edit an already-applied migration; add a new one. Never run destructive database commands (drop/truncate/reset) on a database with data without explicit approval.
4. Never place secrets or credentials in project files. Use user-secrets or environment variables; keep local settings files (`appsettings.*.local`, `.env*`) out of anything shared or backed up.
5. Record every created/changed file group and migration in `docs/project-state.md` (this replaces git history as the phase traceability record).
6. Claude Code's `/rewind` can undo its own file edits within a session, but it does not replace the backup.
