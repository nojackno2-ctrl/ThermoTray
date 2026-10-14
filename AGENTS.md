# Agent collaboration rules

- Read this file and `AI_HANDOFF.md` before working; inspect Git state first.
- Treat uncommitted changes as potentially owned by another collaborator; never discard them.
- Update `AI_HANDOFF.md` after meaningful implementation, investigation, or validation milestones.
- Do not force-push, merge, rebase, reset, or delete branches without explicit user approval; automatic commit and push to GitHub are authorized once verified (see below).
- Do not claim hardware readings, builds, or installer behavior are verified without evidence.

## Automatic commits and push to GitHub (user authorization, 2026-10-14)

- The user has authorized automatic local commits and automatic push to GitHub for all projects.
- After completing a task and appropriate verification, commit the task changes and push to GitHub (`origin`) without asking for confirmation again; do not create empty commits.
- Review the diff and preserve existing work. Include unrelated pre-existing changes only when the user explicitly requests committing them. Never commit secrets, credentials, or personal runtime data.
- This standing authorization covers local commits and pushing verified work to GitHub. Destructive operations (force-push, release, merge, rebase, reset, branch deletion) still require explicit authorization.
- A Git `post-commit` hook is configured in `.git/hooks/post-commit` to automatically push the current branch to `origin` upon commit.
- Record what was verified and any unverified behavior in `AI_HANDOFF.md`; never present a commit as proof that functionality works.
