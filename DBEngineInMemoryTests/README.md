# DBEngine in-memory tests

Run `dotnet test DBEngineInMemoryTests/DBEngineInMemoryTests.csproj` from the repository root.
These tests do not open database connections or modify files outside build/test output.

`NavigationRefreshTests` exercises the tracker and ObjectFromReader using precompiled map
actions. It covers invalidation before computed getters, equal and changed concurrency
tokens, mapped null/empty navigation data, ordinary attachment, and in-place refreshes.
