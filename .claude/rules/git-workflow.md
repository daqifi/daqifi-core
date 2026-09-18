# Git Workflow Rules

CONTRIBUTING.md owns the contributor-facing process (branch names, PublicAPI tracking, the
CA2007/IDE0161/CA1707 build rules). These are the guardrails for agents working in this repo.

## Never push to `main`

- `main` is gated by a merge queue: squash-only, CI's `build` check required. Every change
  lands through a pull request from a feature branch.
- **NEVER** `git push origin main`, and never bypass the ruleset.
- Branch from a fresh `origin/main` (`git fetch origin` first), not from local `main`, which
  can silently drift behind or ahead of the remote.
- Branch names: `feature/`, `fix/`, `chore/` or `docs/` + a short description.

## Commits and pull requests

- Titles use the conventional-commit style already in `git log`: `type(scope): summary`, e.g.
  `fix(sdcard): ...`, `feat(mcp): ...`, `chore(api): ...`; `!` after the scope marks a breaking
  change. The squash merge uses the PR title, so the title is what ends up on `main`.
- PR descriptions lead with what was wrong in plain, user-facing terms, then how it was
  fixed; detail goes below. Reference the issue (`closes #N`).
- CI must be green before the PR goes into the queue.

## Don't use `git stash`

The stash stack is shared by every worktree of this repo, and parallel sessions each work in
their own worktree. A `stash pop` can hand you someone else's changes. Set work aside with a
WIP commit instead.

## If you started work on `main`

```bash
git switch -c fix/description       # keep the work on a new branch
git branch -f main origin/main      # put local main back where the remote is
```
