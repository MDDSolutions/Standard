# CLAUDE.md

This file exists only so that Claude Code loads the repository's guidance. All of it lives in
[AGENTS.md](AGENTS.md), which applies to every AI agent. Do not add Claude-specific guidance here —
put it in AGENTS.md so one document stays authoritative.

@AGENTS.md

@StandardAgentRules.md

[StandardAgentRules.md](StandardAgentRules.md) carries the rules that govern every session in every
repository under `C:\Dev`: the absolute Git and SQL Server prohibitions, how database work is done,
Git checkpoint guidance, build and verification, cross-project changes, date and time conventions,
and the `C:\Dev` sandbox. It is byte-identical in every repository.

**If neither import above resolved, stop and tell the user before changing anything.** Do not proceed
on the assumption that the rules are unimportant — they are non-negotiable, and in particular you
must not make any Git mutation or execute any SQL against any server.

## Settings File Integrity

`.claude/settings.json` and `.claude/settings.local.json` carry the permission rules that enforce the
prohibitions in `StandardAgentRules.md`. Claude Code silently ignores a settings file it cannot
parse: a single missing comma disables every rule in that file, with no warning and no visible sign
that anything is wrong. The session then looks normal while running with no Git or SQL guardrails at
all.

**If a settings file in scope cannot be parsed as JSON, stop and tell the user before doing anything
else.** Do not edit files, run commands, or continue the task until it has been repaired. A
malformed settings file is a hard stop, not a warning — treat the session as having no permission
rules, because that is exactly what it has. Validate again after anything writes to a settings file,
including `/auto-mode-setup`.

(This is the one piece of Claude-specific guidance that belongs in this file rather than in
`AGENTS.md`: `.claude/settings.json` is read only by Claude Code.)
