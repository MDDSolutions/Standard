# Invalidating navigation data on refresh

Use `[DBInvalidateOnRefresh]` on a readable, writable reference property whose cached
navigation data becomes stale when its owner is refreshed from the database:

```csharp
[DBInvalidateOnRefresh]
public IList<VideoFile> VideoFiles { get; set; }
```

ObjectFromReader clears marked properties before mapping a row. When that row merges
into an existing tracked object, the result's property map determines whether each
marked property was supplied. Mapped navigation data replaces the previous value,
including a mapped null or an empty collection. An omitted property becomes null.
This happens before computed getters participate in the ordinary value merge, even
when the parent's concurrency token has not changed.

The policy applies to database materialization, including nested navigation mapping,
deferred tracking of joined lists, and update results mapped into an existing object.
It does not change ordinary GetOrAdd/CopyValues attachment semantics or unmarked
properties. It does not perform lazy loading or cascade invalidation to child objects
held elsewhere; callers reload a null navigation property through their normal path.

When navigation data comes from a later result set or a row callback, assign it after
reading the owner row, as usual. The header refresh invalidates the old value first;
the subsequent assignment supplies the newly loaded data.
