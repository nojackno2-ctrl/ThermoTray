# Agent collaboration rules

- Read this file and `AI_HANDOFF.md` before working; inspect Git state first.
- Treat uncommitted changes as potentially owned by another collaborator; never discard them.
- Update `AI_HANDOFF.md` after meaningful implementation, investigation, or validation milestones.
- Do not push, merge, rebase, reset, or delete branches without explicit user approval.
- Do not claim hardware readings, builds, or installer behavior are verified without evidence.

## Automatic commits (user authorization, 2026-10-05)

- The user has authorized automatic local commits for all projects. After completing a task and appropriate verification, commit the task changes without asking for confirmation again; do not create empty commits.
- Review the diff and preserve existing work. Include unrelated pre-existing changes only when the user explicitly requests committing them. Never commit secrets, credentials, or personal runtime data.
- This standing authorization covers local commits only. Push, release, merge, rebase, reset, force-push, branch deletion, and destructive operations still require explicit authorization.
- Record what was verified and any unverified behavior in `AI_HANDOFF.md`; never present a commit as proof that functionality works.
