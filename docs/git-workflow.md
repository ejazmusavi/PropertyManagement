# Git Workflow

1. **Before work:** run `git status` and `git log --oneline -5`. If there are uncommitted changes you did not make, leave them untouched; work around them or ask.
2. **Branch:** use the branch the session/user specifies (one branch per phase, e.g. `phase-03-pricing`, if none specified). Do not commit to `main` directly unless told.
3. **Never overwrite unrelated changes:** stage explicit paths (`git add <paths>`), never `git add -A` blindly; review `git diff --staged`.
4. **Commit after a phase (or coherent milestone) is built and tested.** Message: `Phase N: <summary>`, body listing modules, migrations, test result. Keep project-state.md update in the same commit so each phase is traceable; record the commit hash in project-state.md in a following small commit if needed.
5. **Never commit** secrets, credentials, connection strings with passwords, `.env`, `appsettings.*.local`, user secrets, keystores, or local IDE/tooling config. Check `.gitignore` covers `bin/`, `obj/`, `.env*`, uploads, and local settings before the first commit of code.
6. **No** `reset --hard`, `rebase`, `push --force`, history rewriting, or branch deletion without explicit authorization.
7. Push only to the designated branch; if a push is rejected, fetch and merge rather than force.
