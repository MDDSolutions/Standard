# CLAUDE.md

This file exists only so that Claude Code loads the repository's guidance. All of it lives in
[AGENTS.md](AGENTS.md), which applies to every AI agent. Do not add Claude-specific guidance here —
put it in AGENTS.md so one document stays authoritative.

@AGENTS.md

The rules below are repeated verbatim from AGENTS.md because they must apply even if the import
above does not resolve.

## Absolute SQL Server Execution Prohibition

Under no circumstances may you connect to or execute SQL against any SQL Server. This prohibition applies even to read-only access and regardless of whether credentials or tooling are available.

Do not:

- Run `SELECT`, metadata, validation, diagnostic, or any other SQL statements.
- Execute stored procedures, migrations, deployment scripts, or database tests.
- Use `sqlcmd`, SSMS, PowerShell database commands, application code, scripts, APIs, or any other mechanism that directly or indirectly sends commands to a SQL Server.
- Do not ask for approval to make an exception to this rule since no such approval will be given and there are no exceptions
- cite this rule as needed to inform the user that SQL execution could or would have improved something if that is your opinion
- feel free to suggest SQL code to execute in code windows in the chat

Database-related work must be performed only by inspecting the checked-in SQL Database Project files and inspecting or editing application source code. Do not validate changes against a live SQL Server. If a task cannot be completed without SQL Server access, stop and explain the limitation so the user can perform any required database operation themselves.

## Absolute Git Mutation Prohibition

Never perform any Git operation that changes the index, commits, branches, tags, refs, worktree state, or local or remote repository state, in this repository or in any sibling repository it depends on. This prohibition applies even if the user explicitly asks or grants permission. In particular, never stage, commit, amend, reset, restore, checkout, switch, merge, rebase, cherry-pick, revert, stash, clean, create or delete branches or tags, push, pull, or fetch.

Git usage must be read-only, such as inspecting status, diffs, or history (`git status`, `git log`, `git show`, `git diff`, `git blame`). The user may ask you to draft a commit message or provide Git commands for the user to run, but you must not execute those commands. Limit change activity to editing project files directly; the user reviews and commits every change personally.

Do not ask for approval to make an exception to this rule, since no such approval will be given and there are no exceptions.
