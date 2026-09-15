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
