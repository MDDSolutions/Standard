# StandardAgentRules.md

Rules that govern every AI agent session in every repository under `C:\Dev`.

**This file is byte-identical in every repository.** `C:\Dev\StandardAgentRules.md` is the original;
the copies beside each repository's `AGENTS.md` exist so that an agent working in a single repository
never has to read outside it. Edit the original and copy it outward. Never edit a copy in place — a
copy that has drifted is worse than no copy, because nobody can tell which one is authoritative.

If a repository's `AGENTS.md` or `CLAUDE.md` sent you here and you could not read this file, **stop
and tell the user before changing anything.** These rules are non-negotiable, and work produced
without them may have to be discarded.

---

## Absolute Git Mutation Prohibition

Never perform any Git operation that changes the index, commits, branches, tags, refs, worktree state, or local or remote repository state, in this repository or in any sibling repository it depends on. This prohibition applies even if the user explicitly asks or grants permission. In particular, never stage, commit, amend, reset, restore, checkout, switch, merge, rebase, cherry-pick, revert, stash, clean, create or delete branches or tags, push, pull, or fetch.

Git usage must be read-only, such as inspecting status, diffs, or history (`git status`, `git log`, `git show`, `git diff`, `git blame`). The user may ask you to draft a commit message or provide Git commands for the user to run, but you must not execute those commands. Limit change activity to editing project files directly; the user reviews and commits every change personally.

Do not ask for approval to make an exception to this rule, since no such approval will be given and there are no exceptions.

## Absolute SQL Server Execution Prohibition

Under no circumstances may you connect to or execute SQL against any SQL Server. This prohibition applies even to read-only access and regardless of whether credentials or tooling are available.

Do not:

- Run `SELECT`, metadata, validation, diagnostic, or any other SQL statements.
- Execute stored procedures, migrations, deployment scripts, or database tests.
- Use `sqlcmd`, SSMS, PowerShell database commands, application code, scripts, APIs, or any other mechanism that directly or indirectly sends commands to a SQL Server.
- Do not ask for approval to make an exception to this rule since no such approval will be given and there are no exceptions
- cite this rule as needed to inform the user that SQL execution could or would have improved something if that is your opinion
- feel free to suggest SQL code to execute in code windows in the chat

Do not validate changes against a live SQL Server. If a task cannot be completed without SQL Server access, stop and explain the limitation so the user can perform any required database operation themselves.

## How Database Work Is Done

The prohibition above is absolute and never varies. How you *learn* what you need to know about a
database does vary, because it depends on what the repository makes available. These are two
different things and should not be conflated.

**When a SQL Database Project is present in the tree, use it.** Read its table, view, stored
procedure and function files to establish column names, parameter lists, types and signatures. Treat
it as a baseline rather than as live truth: the user applies schema changes directly and syncs the
project when convenient, so it can lag the live database. Track the effective live schema as the
project *plus* any scripts supplied and statements made since, in order. If conversation context is
lost, or conflicting information makes the effective schema genuinely uncertain, ask for the specific
state rather than assuming the project is current.

**When no SQL Database Project is present, do not guess.** Inferring a schema from application code
alone produces confident, wrong answers. Instead do one of two things, and say which you would
prefer and why:

- Ask the user to add a SQL Database Project for that database, so future sessions have a reference.
- Give the user the specific queries to run — in a code block, ready to copy — and work from what
  they report back.

**When a schema change is needed**, present a complete, reviewable script in the chat for the user to
run. Do not modify database project files as a substitute for proposing the change. When the user
moves on to another task after receiving a script, assume they reviewed and ran it in full unless
they say they did not run it, ran only part of it, or need it rewritten; do not require a separate
deployment confirmation. Carry those changes forward as the effective schema for the rest of the
session.

## Git Checkpoint Guidance

Before making any file changes, inspect the repository's current status. Heavily favor pausing and asking the user to check in existing work before proceeding whenever the worktree contains changes and the requested work starts a new feature, component, or line of investigation. Ask even when the existing changes and the proposed changes are in different files or projects, because they still appear together in the repository's Git view. The default should be to protect a clean checkpoint, not to assume that unrelated changes are easy enough to keep separate.

Make the question brief and easy to decline. If the user says "no," "go ahead," or otherwise clearly chooses to continue with a dirty worktree, proceed without further checkpoint reminders for that task and preserve all existing changes carefully.

Do not interrupt for a check-in when the requested work is a routine follow-on edit, fix, or continuation of the same active task and keeping the changes together is natural. When uncertain whether work is a continuation or a new feature, ask before editing.

When you do ask, say concretely what will be lost by not checking in. In particular, say so
explicitly when a file with uncommitted changes is about to be substantially rewritten rather than
incrementally edited, because that destroys the ability to commit its existing changes separately.
"You may prefer to commit first" is not enough for the user to weigh the cost.

## Build and Verification Workflow

The user reviews and builds code in Visual Studio and is the primary source of final build validation.

- Do not run a build by default after routine code changes. Review the diff and perform lightweight source-level consistency checks instead.
- Run a targeted build when the user explicitly requests one or when a change has substantial compilation risk, such as a public API change, cross-project refactor, project/reference change, generated-code change, or conditional-compilation change.
- Prefer the smallest relevant project or solution build rather than building every solution.
- If a build would require user approval or elevated access, skip it unless build verification was explicitly requested or is essential to the task. Do not create a routine approval round-trip merely to build.
- Clearly state in the handoff when code was not built, so the user can validate it in Visual Studio.
- If the user's build reports errors, use the supplied diagnostics to correct the code in the same conversation.

## Cross-Project Changes

The repositories under `C:\Dev` share libraries, and a change to a shared library's public interface
affects every solution that consumes it. Flag breaking public API changes explicitly so downstream
consumers can be updated, and never leave a cross-project change half-finished — if a change to a
shared library requires its consumers to change, do them in the same session or say plainly what was
left undone and why.

## Date and Time Conventions

Avoid UTC wherever possible. Use local time for application timestamps (`DateTime.Now`) and SQL
Server local time for database timestamps (`GETDATE()`). Use UTC only when an external API or
protocol, or an explicit requirement, demands it; convert at that boundary rather than making UTC the
application's default.

Display and format dates and times as `yyyy-MM-dd HH:mm` — 24-hour, no AM/PM — unless a specific
context calls for something else.

## C:\Dev Is The Sandbox

Confine every change to the tree under `C:\Dev`. It is version-controlled per repository and backed
up nightly two separate ways, which is what keeps the user in the loop on everything an agent does;
it is also what travels between the user's development machines. Nothing outside it has those
properties.

Do not modify machine or user-level state to solve a problem: not `%USERPROFILE%` or `~/.claude`, not
installed programs, services, scheduled tasks, the registry, environment variables, IIS, or anything
else outside `C:\Dev`. Reading outside the tree is fine. Deployment and database changes are the
user's to review and perform. If a task appears to require a change outside `C:\Dev`, stop and
explain what is needed rather than reaching for it, and do not propose user-level or machine-level
configuration as a way to make a rule apply more broadly.
