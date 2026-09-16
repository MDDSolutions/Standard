# Menu system

A reusable application launcher: a category tree, favourites, recent items, search, in-application
menu editing and Quick Run, backed by a `MenuSystem` schema in SQL Server. The code spans three
projects - `Standard/MDDFoundation/Menus` (model), `Standard/DBEngine/Menus` (providers, editor, user
store, Quick Run, install script) and `Common/MDDWinForms/Menus` (`MenuLauncherForm`, dispatcher, edit
dialogs) - and its tests are `Standard/MenuSystemUnitTests`.

A host application supplies the application-specific parts through its own host class: the instance
qualifier, window-position persistence, target factories, any legacy command hooks, and extra pages
added through `MenuLauncherForm.AddPage`. For the host in this tree, see
`Other/VideoCurator/MenuSystem.md`.

## Database

The schema lives in `MenuSystem` and is mirrored in the host application's database project, which is the
authoritative reference - in this tree `Other/OtherDB/MenuSystem/Tables` and
`.../Stored Procedures`. It was built up
through a series of scripts run in SSMS and synced back; those scripts have been deleted, since the
objects they created are now in OtherDB and their data migrations were one-time.

To install the feature into a different database, use
`Standard/DBEngine/Menus/Install-MenuSystem.sql` - a single application-independent script that
creates the schema, tables and procedures and seeds nothing. It lives beside the code it serves, and
refuses to run where MenuSystem already exists. As the feature evolves it is kept current, so it
always installs the present shape from scratch; changes to an existing database come as transform
scripts in conversation instead.

| Table | Holds |
|---|---|
| `Application` | One row per application key, with `SimpleMode` and `UsageRetentionDays` |
| `MenuItem` | Categories and actions, with `Retired` for soft deletion |
| `MenuItemCategory` | Which categories an action appears in, and its order in each |
| `QuickRunParameter` | Saved defaults and last values per stored procedure parameter |
| `User` | One row per Windows account, keyed by SID |
| `UserFavourites` | Per-user favourites and their order |
| `UserSetting` | Per-user, per-application preferences (currently `DefaultView`) |
| `UsageLog` | One row per successful launch, pruned by retention |

Existing SQL permissions must allow SELECT on the menu tables and EXECUTE on the `MenuSystem`
procedures for the runtime account. Menu definitions are trusted application configuration: process
targets and reflected controls execute with the user's application permissions. Provider validation
rejects cycles and parents that are not categories; the database also enforces application boundaries
and basic target constraints.

## Reuse

- `MDDFoundation.Menus.MenuItem`: positive integer ID, Category/Action kind, display/search metadata and launch target. Categories nest through `ParentId`; actions leave it null and carry `Categories`, a list of `MenuCategoryRef`. IDs identify menu entries, not windows.
- `IMenuProvider`: asynchronous definition loading by application key. `JsonMenuProvider` uses a dictionary keyed by application name; see the sample beside the host doc, where each action carries a `Categories` array of `{ CategoryId, SortOrder }` instead of a `ParentId`. JSON IDs are independent of database identity values; do not switch providers expecting local favorites to map unless IDs are aligned.
- `MDDDataAccess.Menus.DatabaseMenuProvider`: loads the MenuSystem definitions from the supplied connection/DBEngine.
- `MDDWinForms.Menus.WinFormsMenuDispatcher`: reflected form/control construction, reuse through Application.OpenForms (including ControlForm contents), restoration/activation, and fire-and-forget process launching. Set `UseInstanceQualifier` on the dispatcher, not individual items. For external processes set TargetKind=Process, ExecutablePath, Arguments, and DefaultLaunchMode=CreateNew.
- `MenuLauncherForm`: category tree, cross-category search, favorites, recent ordering, single click to select, double-click/Enter to open, right-click for Open/Activate and Open New (when allowed), Ctrl+Enter and a visible button to request another instance, Ctrl+F to search, Escape to clear.
- `MenuLauncherApplicationContext`: ExitApplication coordinates same-process closing with cancellation; CloseLauncher is for launching independent processes. Cross-UI-thread discovery and cross-process coordination are future work.

Ordinary targets only need a public parameterless constructor and a database/config entry with their full type name and assembly name. A host may also register optional factories for targets with constructor dependencies, and explicit legacy hooks for operations that are not forms - those are host code, not reflection targets.

Favourites and usage history live in the database (see **Users, favourites and usage**); there is no local preference file. IconKey is reserved metadata in v1; the first UI uses text entries. Recently used is ordered newest first.

The tree lists Favorites, Recently used, the categories, and finally **(All Items)** - every action,
ordered favourites first, then recently launched, then the rest. It sits last because it is a fallback
rather than a place to work.

The launcher opens on an explicitly chosen view when there is one: right-click any tree node and pick
**Open on this view**, stored per user and per application in `MenuSystem.UserSetting` (the table is
generic, so later preferences need no schema change). With no choice stored it opens on Favorites if
the user has any, else Recently used if there is any history, else the first category - reaching
(All Items) only when there is nothing else.

## Users, favourites and usage

Users, favourites and usage are held in:

- Two columns on `MenuSystem.Application`: `SimpleMode` (default 1) and `UsageRetentionDays` (default 90, NULL to keep usage forever).
- `MenuSystem.User` — one row per Windows account. `IdentityKey` is the account SID (stable across renames); `DisplayName` is the readable `DOMAIN\user` name and is refreshed on each open.
- `MenuSystem.UserFavourites` — `(UserId, MenuItemId)` with a positive `SortOrder` that drives both the Favorites view and the favourite block at the top of Home.
- `MenuSystem.UsageLog` — one row per successful launch, with launch mode, a per-process `SessionId` and the machine name.
- `MenuSystem.User_Open`, `UserState_Select`, `UserFavourites_Set`, `UsageLog_Record` and `UsageLog_Prune`.

Simple mode: an application row with `SimpleMode = 1` registers any Windows account that runs it and shows that account the whole menu. Every procedure refuses to run against an application with `SimpleMode = 0`, so the restricted variant fails closed until per-user menu subsets are implemented. That is the intended seam for partial application security; nothing else needs to change in the client.

Auditing is a log rather than a single last-used row, so the same table answers both "what did I run last" and "what gets used at all". Recently used is `MAX(created_date)` per item from the log. Pruning is deliberately cheap: `User_Open` and `UsageLog_Record` each run one 500-row `UsageLog_Prune` batch, which deletes only rows older than the owning application's `UsageRetentionDays`. Steady-state usage generates far fewer rows per launch than that, so the log stays inside retention without a job. `EXEC MenuSystem.UsageLog_Prune` with a larger `@BatchSize`/`@MaxBatches` from a scheduled task if a backlog ever builds up, or to clear one after changing a retention setting.

`DatabaseMenuUserStore` (in DBEngine) is the `IMenuUserStore` implementation; `MenuUserPreferences` in MDDFoundation is the snapshot it returns. The launcher reloads that snapshot after each favourite change, so a second running instance picks up the change on Reload.

## Categories

An action belongs to **one or more** categories; a category still has exactly one parent, so the
left-hand tree stays a tree. `MenuSystem.MenuItemCategory` holds the membership, and `ParentId` on
`MenuItem` is now categories-only — `CK_MenuSystem_ActionPlacement` enforces that actions leave it
null. So "Performer Search" can sit in both a Searching category and a Performers category without
being duplicated as a row.

`SortOrder` lives on the membership, so an item can be first in one category and third in another.
`MenuItem.SortOrder` remains the fallback used by Home, Favorites, Recently used and search results,
where no single category is in view.

The **Category** column follows what is being browsed: inside a category it shows that category's
path, and everywhere else (Home, Favorites, Recently used, search) it shows every category the item
is in, joined with `; `. Search matches against all of an item's category paths. An item in several
categories is still listed once outside those categories.

"At least one category" is not expressible as a `CHECK` constraint — the `MenuItem` row necessarily
exists before its first membership row, so a constraint firing on insert would reject the insert
that is about to become valid. It is enforced three ways instead: `trgMenuItemCategory_Validate`
refuses to delete an action's last membership (and rejects placing a category in a category, or an
action under a non-category), `MenuDefinition.Validate` refuses to load a menu containing an
uncategorized action. This query lists any offenders, and should return nothing:

```sql
SELECT Id, Title FROM MenuSystem.MenuItem item
WHERE Kind = 1 AND Retired = 0 AND NOT EXISTS(SELECT 1 FROM MenuSystem.MenuItemCategory placed
    WHERE placed.ApplicationKey = item.ApplicationKey AND placed.MenuItemId = item.Id);
```
Use `MenuSystem.MenuItemCategory_Set` to add an item to a category or move it within one; it appends
to the end of the category when `@SortOrder` is omitted.

## Editing the menu

`MenuItem.Retired` and these procedures are what back in-application editing: `MenuItem_Save`, `MenuItem_Retire`, `MenuItem_Restore`, `MenuItemCategory_Remove`,
`MenuItem_MoveCategory` and `MenuItemCategory_Move`.

Editing appears only when the host sets `MenuLauncherForm.Editor`. A launcher without one - the JSON
provider, or any application that has not opted in - shows no editing affordances at all. The host supplies
`DatabaseMenuEditor` if it wants editing. Note the asymmetry with favourites: favourites are per
user, whereas **every menu edit is shared** by all users of the application key.

- **Edit / Delete** are on the right-click menu of both the category tree and the item list.
- **Add...** beside Toggle favorite asks for a category or a menu item. Adding while a category is
  selected pre-fills that category, which also satisfies the "at least one category" rule.
- **Move Up / Move Down** reorder a category among its siblings, or an item within the category being
  browsed. Ordering is per placement, so the entries only appear inside a category, never on Home,
  Favorites, Recently used, or a search result.

The edit dialog shows only the fields valid for the chosen target kind, because `CK_MenuSystem_Target`
accepts specific combinations - a Control needs `TargetTypeName`, a Process needs `ExecutablePath` and
`CreateNew`, a StoredProcedure needs `ProcedureName`. It runs `MenuDefinition.Validate` over the whole
definition before saving, so the rules that gate loading a menu also gate writing one, and constraint
violations are not how you find out about a mistake.

### Delete is a retire

`MenuItem` cannot simply be deleted: `UsageLog` and `UserFavourites` both hold foreign keys to it, so
a plain `DELETE` fails for anything anyone has ever launched or favourited. Delete therefore sets
`Retired = 1`, and `DatabaseMenuProvider` filters retired items and their placements out of the menu.
Usage history survives, the item can be brought back with `MenuItem_Restore`, and the mechanism is the
same one a future per-user menu subset would use. Retiring an action also drops its favourites, which
are preferences rather than history.

Deleting a category that still holds anything asks where its contents should go and reassigns them
first. The destination placement is written before the old one is removed, because
`trgMenuItemCategory_Validate` refuses to remove an action's last category.

Both move procedures densify the order within the affected group before swapping, so existing rows
with duplicate or sparse `SortOrder` values reorder predictably rather than appearing to do nothing.

## Validation

Build with Visual Studio MSBuild using the host solution's project configuration mappings (`/t:VideoCurator /p:Configuration=Debug`). Do not start the host merely to validate the code: startup accesses SQL Server. Offline checks cover definition validation, JSON loading, reflected instance reuse/new instances, ControlForm wrapping, qualifier behavior and the user store contract. Interactive validation against a real database remains a user step after applying the SQL script.

`MenuSystemTests` and `QuickRunTests` in `Standard/MenuSystemUnitTests` run in Test Explorer. They use synthetic forms for the launch and lifetime tests, and drive the launcher through in-memory `IMenuUserStore` and `IMenuEditor` fakes, so they never open a connection. WinForms work runs on an STA thread with a 30-second bound, so a UI test that stops pumping fails rather than hanging the run.
Covered: reordering, selection surviving a reload, the absence of editing affordances without an editor, and the edit dialog's layout and target-kind field switching (it is constructed and inspected without being shown modally). Not covered: the modal flows themselves - actually saving through Edit, confirming a Delete, or choosing a kind in Add - which a headless fixture cannot drive. Check those by hand.

Two things are deliberately not covered, because both need a real WinForms message loop that a test
host does not provide - on a bare STA thread their continuations never arrive and the test hangs
rather than fails: the Quick Run runner's execute/cancel/recover lifecycle, and window reuse per
procedure (which also reaches for `DBEngine.Default`). Check those by hand.

The dialog layout checks exist because the edit dialog first shipped collapsed to a sliver: a Form that auto-sizes around docked children has no width to hand them. Asserting on ClientSize and the editor widths catches that class of mistake, which compiling never will. All checks passed, including search and shutdown cancellation. No SQL was executed.

Date/time convention: every MenuSystem table uses created_date (DATETIME, GETDATE() default) and nullable modified_date maintained by a set-based update trigger. Usage timestamps are SQL Server local time; `RecordUsageAsync` returns the instant the database wrote so the launcher never mixes in a client clock.

## Quick Run menu items

Quick Run is an Action with TargetKind=StoredProcedure (database value 2) and ProcedureName set to the schema-qualified stored procedure name, preferably `[schema].[procedure]`. TargetTypeName and ExecutablePath are null. The launcher opens FormsDataAccess.QuickRunControl through reflection, so MDDWinForms does not acquire a database dependency. The application must reference FormsDataAccess and DBEngine and initialize DBEngine.Default. Missing support is reported when launching the item. Window reuse distinguishes procedure names; imported names are canonical and schema-qualified.

Quick Run entries are ordinary menu items now: create them through the launcher like anything else,
with the target set to a stored procedure. `MenuSystem.QuickRunParameter` keeps defaults and last
values per procedure/parameter, database-wide rather than per user.

The runner discovers typed parameters through DBEngine/SqlClient. Users can select saved defaults or last values, edit values, save defaults on execution, or omit a parameter to use the stored procedure's own default. SQL Server does not expose T-SQL default expressions through DeriveParameters; the editor does not invent those defaults. NULL text sends SQL NULL; empty text remains an empty string. Typed binary input uses hexadecimal digits. Table-valued and CLR user-defined parameters report an explicit unsupported-type error in this first version.

Execute captures values before starting work. Defaults/last values are saved on an execution attempt, as in the old runner. A Stopwatch measures elapsed duration; Cancel requests SQL cancellation, and a busy window defers closing until the operation stops. PRINT/informational messages appear as they arrive, and output parameters/return status appear after execution completes. Returned recordsets are not displayed yet. The new launcher no longer loads the legacy Quick Run dropdown; the original menu retains its old implementation for comparison.

`QuickRunTests` covers SQL NULL and typed value conversion and validation of a stored-procedure menu entry. The runner's own lifecycle is checked by hand, for the reason given under Validation.
