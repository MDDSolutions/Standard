# AGENTS.md

This file provides guidance to AI agents when working with code in this repository.

## Standard Agent Rules

Read [PortableAgentRules.md](PortableAgentRules.md) on every machine.

On MDD-OWUI01, for repositories under `C:\Dev`, read
[C:\Dev\StandardAgentRules.md](C:/Dev/StandardAgentRules.md) before doing the task.
This is the only installation authorized to use that file's Git mutation workflow;
a matching path on another machine does not grant that authorization.

If the local file cannot be read, pause the original task and diagnose and repair access
within existing permissions. Take only the actions necessary to restore access; do not
invent replacement rules or bypass permissions. A process initialization failure is not
proof that the file is unreadable: retry through a working shell. If repair needs the
user's help or approval, explain the specific problem and request it. After successfully
reading the valid rules, resume the original task.

## DBEngine: Updates Return The Row

An update that does not return the row it changed leaves the application object stale — trigger-set
`modified_date` values, identities, and any status or computed column the procedure decided on are
all missing. That also makes the object unsafe to put under DBEngine's `Tracking`.

`RunSqlUpdate` / `RunSqlUpdateAsync` are the intended mechanism: they run a procedure (or an ad-hoc
statement, with `IsProcedure: false`), require exactly one row in the result, merge it into the
typed object via `ObjectFromReader`, feed the `Tracker`, and return `false` if no row came back.
`DBUpsert` routes through the same path.

`SqlRunProcedure` / `SqlRunStatement` and friends write without merging anything back. Prefer them
only for commands that genuinely change no row the caller holds. When adding a save procedure,
finish it with a single-row `SELECT` of the affected row, shaped like the object that maps it.
