# AGENTS.md

This file provides guidance to AI agents when working with code in this repository.

## Standard Agent Rules

The rules that govern every session in every repository under `C:\Dev` live in
[StandardAgentRules.md](StandardAgentRules.md), a byte-identical copy of which sits beside this file.
**Read it before making any change.** It covers the absolute Git and SQL Server prohibitions, how
database work is done, Git checkpoint guidance, build and verification, cross-project changes, date
and time conventions, and the `C:\Dev` sandbox.

If you cannot read that file, stop and tell the user before changing anything. Those rules are
non-negotiable.

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
