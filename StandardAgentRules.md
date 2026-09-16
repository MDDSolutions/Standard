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

## Read-Only SQL Server Access

Agents may connect to an explicitly authorized SQL Server database for read-only investigation when doing so materially helps the current task. Permitted uses include inspecting schema metadata, examining object definitions, validating assumptions about stored data, and diagnosing application behavior.

Read-only access is permission to observe, not permission to administer or modify.

### Required Access Controls

Agents may connect to SQL Server only by using the SQL-authenticated login `AIAgentReadOnly`. No other SQL Server or Windows identity is authorized for agent use, even if it appears to have read-only access.

Do not use Windows or integrated authentication, the agent process identity, the user's identity, application credentials, deployment credentials, cached credentials, or any other SQL login. Do not ask for or attempt to discover an alternative credential.

If the password for `AIAgentReadOnly` is unavailable, the login does not exist, authentication fails, access to the required database is denied, or the account otherwise does not work, stop and inform the user. Do not retry through another identity or authentication method.

The `AIAgentReadOnly` identity must have only the minimum necessary permissions, normally:

- `CONNECT`
- `SELECT` on the required schemas, tables, or views
- `VIEW DEFINITION` within the required database when object definitions are needed

The identity must not have write, ownership, administrative, deployment, or unrestricted execution permissions. If query results or permission metadata indicate that `AIAgentReadOnly` has such access, stop without performing further database work and inform the user.

After connecting, verify with a read-only query that `ORIGINAL_LOGIN()` is exactly `AIAgentReadOnly` before performing investigative queries. If it is not, stop and inform the user.

Connection-string options such as `ApplicationIntent=ReadOnly` do not by themselves establish that an identity is read-only.

### Connection and Credential Configuration

The authorized connection is:

- Server: `MDD-SQL2022`
- Database: `Other`
- SQL-authenticated login: `AIAgentReadOnly`

The connection settings are stored as persistent Windows user environment variables:

- `SQLCMDSERVER` contains the server name.
- `SQLCMDDBNAME` contains the database name.
- `SQLCMDUSER` contains the login name.
- `SQLCMDPASSWORD` contains the password.

These variables are inherited when Codex or Claude Code starts. Agents may read them only for the authorized SQL connection. Never display, echo, log, return, or write the password to a command line, chat, source file, script, configuration file, or diagnostic output. Never pass the password with the `sqlcmd -P` option. Do not create, change, delete, or persist any of these environment variables; the user manages them.

If `SQLCMDPASSWORD` is absent or empty, stop and inform the user. Do not search for the password elsewhere and do not request or try another credential. The non-secret variables may be checked against the fixed values above, but the fixed values above remain authoritative and no value may be substituted to reach another server, database, or login.

For `sqlcmd`, use SQL authentication with the fixed server, database, and login, allowing `sqlcmd` to obtain the password from `SQLCMDPASSWORD`. Do not specify `-E` or another authentication method. A permitted connection has this form:

```powershell
sqlcmd -S 'MDD-SQL2022' -d 'Other' -U 'AIAgentReadOnly' -Q "SELECT ORIGINAL_LOGIN() AS LoginName, SUSER_SNAME() AS SessionLogin, DB_NAME() AS DatabaseName;"
```

Every new connection must first verify that `ORIGINAL_LOGIN()` is `AIAgentReadOnly` and that `DB_NAME()` is `Other`. This verification may be the first `SELECT` in the same batch as the investigative query.

Within Codex on this machine, the normal restricted execution sandbox cannot complete the SQL client's TLS connection. Run the same `AIAgentReadOnly` SQL-authenticated command through Codex's approved outside-sandbox execution path. This is only an execution-environment requirement; it does not authorize a different login, authentication method, server, database, or broader filesystem or machine access. Claude Code may use its normal execution path if the authorized connection succeeds there.

### Permitted Queries

Agents may issue direct, read-only `SELECT` queries, including queries beginning with a common table expression (`WITH`) and ending in a `SELECT`.

Permitted targets are limited to the database relevant to the current task:

- User tables and views for necessary diagnostic data
- `sys` catalog views and other read-only metadata
- Object definitions exposed through catalog views or metadata functions
- Aggregate and existence queries used to validate assumptions

Prefer metadata and narrowly targeted queries before retrieving application data. Select only the columns and rows needed for the investigation. Use restrictive predicates and a reasonable `TOP` limit when inspecting row-level data.

### Prohibited SQL Operations

Agents must never execute any operation that can create, alter, delete, insert, update, merge, deploy, restore, import, export, or otherwise mutate database or server state.

In particular, do not execute:

- `INSERT`, `UPDATE`, `DELETE`, `MERGE`, or `TRUNCATE`
- `CREATE`, `ALTER`, `DROP`, `RENAME`, or other DDL
- `SELECT INTO`, including creation of temporary tables
- `EXEC`, `EXECUTE`, dynamic SQL, stored procedures, or extended procedures
- Migrations, deployment scripts, seed operations, or database update commands
- `DBCC`, `BACKUP`, `RESTORE`, `BULK INSERT`, or administrative commands
- Transaction-control statements
- Queries containing write-oriented or aggressive locking hints such as `UPDLOCK`, `XLOCK`, `TABLOCKX`, or `HOLDLOCK`
- Linked-server, external-data, or ad hoc remote-access mechanisms such as `OPENQUERY`, `OPENROWSET`, or `OPENDATASOURCE`
- User-defined or CLR functions when their read-only and side-effect-free behavior has not been established
- Any command intended to test whether the account can write
- Any operation against a database or server outside the task’s explicitly authorized scope

Do not ask for permission to make an exception. If mutation is needed, provide the proposed SQL as text for the user to review and run manually.

### Data Safety and Query Impact

Read access can still expose confidential information or affect production performance.

Agents must:

- Retrieve the minimum data necessary.
- Avoid secrets, credentials, tokens, encryption material, and personal or regulated data unless that exact data is explicitly required and authorized.
- Avoid copying sensitive row data into chat when a count, schema description, redacted sample, or summary is sufficient.
- Avoid unbounded queries and broad `SELECT *` queries against potentially large tables.
- Avoid queries likely to produce substantial load, blocking, or very large results.
- Use a reasonable command timeout and stop if a query appears expensive or disruptive.
- Treat query results as sensitive and disclose only what is needed to complete the task.

Read-only access does not authorize browsing unrelated business data.

### Schema and Database Changes

Agents may inspect the live schema through read-only catalog queries and may also inspect SQL Database Project files when present. The live database is the best source for its current schema, while project files remain useful for understanding intended and version-controlled definitions.

When a schema or data change is needed, prepare a complete, reviewable script in the chat or as a project file, as appropriate. Do not execute it. The user remains solely responsible for reviewing and running every database mutation.

If the required investigation cannot be completed safely through the permitted read-only access, stop and explain what information or user-run query is needed.

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
