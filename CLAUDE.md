# CLAUDE.md

This file exists only so that Claude Code loads the repository's guidance. All of it lives in
[AGENTS.md](AGENTS.md), which applies to every AI agent. Do not add Claude-specific guidance here —
put it in AGENTS.md so one document stays authoritative.

@AGENTS.md

Read AGENTS.md successfully before proceeding; it selects the installation rules
and explains how to recover access if they cannot be read.

## Settings File Integrity

`.claude/settings.json` and `.claude/settings.local.json` carry the permission rules that enforce the
Git and SQL rules in the applicable agent rules. Claude Code silently ignores a settings file it cannot
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
