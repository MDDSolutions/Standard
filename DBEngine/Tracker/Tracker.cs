using MDDFoundation;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace MDDDataAccess
{
    public class Tracker<T> where T : class, new()
    {
        public Tracker(DBEngine dbengine)
        {
            if (!Tracked<T>.IsTrackable)
                throw new InvalidOperationException(
                    $"Type {typeof(T).FullName} is not trackable. A key is required; a concurrency token is optional but recommended."
                );
            DBEngine = dbengine;
        }
        private DBEngine DBEngine { get; }
        private readonly ConcurrentDictionary<object, Tracked<T>> trackedObjects = new ConcurrentDictionary<object, Tracked<T>>();
        /// <summary>
        /// This is the one that will now never be used - the problem was if you put an unbaked T in the tracker and then you puke when trying to finish baking it, you're
        /// left with a mess... so we now fully load the object and then pass it to the other GetOrAdd - there is now a CopyValues function in Tracker<typeparamref name="T"/>
        /// to deal with any merging that needs to happen - this also allowed sequential access on the reader (although the benchmark on it wasn't any better)
        /// Use this method when you are in the process of loading an entity from the database.  The idea is to get the key and concurrency values from the database row,
        /// call this method to get a tracked entity (which may be a new instance, or an existing instance), and then load the data into the entity instance returned if
        /// you still need to.  The out parameter 'entity' will be a new instance if the entity was not already being tracked, or it will be the existing instance if it was.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="concurrency"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public Tracked<T> GetOrAdd(object key, object concurrency, out T entity)
        {
            if (key == null)
                throw new InvalidOperationException($"The key value provided for {typeof(T).FullName} was null");

            T lentity = null;

            var tracked = trackedObjects.AddOrUpdate(
                key,
                k => 
                {
                    lentity = new T();
                    return new Tracked<T>(key, concurrency, lentity);
                },
                (k, existing) =>
                {
                    if (existing.TryGetEntity(out var e))
                        lentity = e;

                    switch (existing.State)
                    {
                        case TrackedState.Initializing:
                            throw new InvalidOperationException("The entity is already Initializing - this should not happen");
                        case TrackedState.Unchanged:
                            //if the existing state is unchanged, but the concurrency value is different, mark it as initializing so it gets reloaded
                            if (Tracked<T>.HasConcurrency)
                            {
                                if (!DBEngine.ValueEquals(concurrency, existing.ConcurrencyValue))
                                {
                                    // remote row has changed compared to tracked copy; mark for reload
                                    existing.Initializing = true;
                                }
                            }
                            else
                            {
                                // No concurrency token: we can't reliably detect DB changes; choose to reuse existing instance.
                                // If you prefer always reloading, set existing.Initializing = true here.
                                existing.Initializing = true;
                            }
                            return existing;
                        case TrackedState.Modified:
                            //if the existing state is modified, the concurrency value must match
                            if (Tracked<T>.HasConcurrency)
                            {
                                if (!DBEngine.ValueEquals(concurrency, existing.ConcurrencyValue))
                                    throw new InvalidOperationException("The entity has been modified and the concurrency value does not match.");
                                return existing;
                            }
                            else
                            {
                                // No concurrency token: we cannot safely reconcile DB row with local modifications.
                                // Throw to avoid data loss.
                                throw new InvalidOperationException("Entity is modified in memory but the type has no concurrency token to validate reloading.");
                            }
                        case TrackedState.Invalid:
                            lentity = new T();
                            return new Tracked<T>(key, concurrency, lentity);
                        default:
                            throw new InvalidOperationException("The entity is in an invalid state.");
                    }
                });
            entity = lentity;
            return tracked;
        }
        /// <summary>
        /// Never mind the comment below - leaving it there because who knows if I change my mind again... this is now the main way to load or "Attach" entities to the tracker
        /// make sure the entity is fully baked / initialized - we don't want any invalid entities in the tracker
        /// This method should probably not ever be used - if you're tracking, don't fully initialize an entity outside of the tracker.
        /// call the other GetOrAdd method with the key and concurrency values instead - it will give you back an uninitialized entity to load into.
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        /// <exception cref="NotImplementedException"></exception>
        public Tracked<T> GetOrAdd(ref T entity)
        {
            var key = Tracked<T>.GetKeyValue(entity);
            if (key == null)
                throw new InvalidOperationException($"The entity provided for {typeof(T).FullName} has a null key value");

            T loading = entity;
            var loadingconcurrency = Tracked<T>.GetConcurrencyValue?.Invoke(loading);
            var tracked = trackedObjects.AddOrUpdate(
                key, 
                k => new Tracked<T>(loading),
                (k, existingtracked) =>
                {
                    if (existingtracked.TryGetEntity(out T existingentity))
                    {
                        if (ReferenceEquals(existingentity, loading))
                        {
                            //entity may have been updated, but now that it has been reloaded, it should be reinitialized
                            existingtracked.Initializing = true;
                            existingtracked.EndInitialization();
                            return existingtracked;
                        }
                        else
                        {
                            switch (existingtracked.State)
                            {
                                case TrackedState.Unchanged:
                                    //object exists and is unchanged in the tracker - if the concurrency property is the same, our load was for naught - we can just return the existing value
                                    //if the object does not have concurrency, I guess we just blindly merge the values from the loaded object - they could be new, but who knows?
                                    //if (DebugLevel >= 200) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 55, "OFR cache hit", r.ToString(), typeof(T).Name));

                                    if (!Tracked<T>.HasConcurrency || !DBEngine.ValueEquals(loadingconcurrency, Tracked<T>.GetConcurrencyValue(existingentity)))
                                        existingtracked.CopyValues(loading, true);
                                    loading = existingentity;
                                    return existingtracked;
                                case TrackedState.Modified:
                                    if (!Tracked<T>.HasConcurrency)
                                        throw new InvalidOperationException("Entity is modified in memory but the type has no concurrency token to validate reloading.");

                                    //if the concurrency value matches, then the existing entity is based on the loading entity so we can safely discard the incoming entity and keep the user's
                                    //pending changes
                                    if (!DBEngine.ValueEquals(loadingconcurrency, existingtracked.ConcurrencyValue))
                                    {
                                        var attemptDirtyAware = DBEngine.DirtyAwareObjectCopy && Tracked<T>.SupportsDirtyAwareCopy;
                                        existingtracked.CopyValues(loading, attemptDirtyAware);
                                    }
                                    loading = existingentity;
                                    return existingtracked;
                                case TrackedState.Initializing:
                                    throw new InvalidOperationException($"There is an object in the {typeof(T).Name} tracker with key {key} in an initializing state - this really shouldn't happen");
                                case TrackedState.Invalid:
                                default:
                                    return new Tracked<T>(loading);
                            }
                        }
                    }
                    return new Tracked<T>(loading);
                });
            entity = loading;
            return tracked;
        }

        public bool TryGet(object key, out Tracked<T> tracked) => trackedObjects.TryGetValue(key, out tracked);
        public int Count => trackedObjects.Count;

        //public IEnumerable<Tracked<T>> Entries => trackedObjects.Values;

        //public IEnumerable<Tracked<T>> GetDirtyEntries()
        //{
        //    foreach (var entry in trackedObjects.Values)
        //    {
        //        if (entry.State == TrackedState.Modified)
        //            yield return entry;
        //    }
        //}

        //public void Detach(object key)
        //{
        //    trackedObjects.TryRemove(key, out _);
        //}
        public void PruneInvalid()
        {
            foreach (var kvp in trackedObjects)
            {
                if (!kvp.Value.Initializing && !kvp.Value.TryGetEntity(out _))
                {
                    trackedObjects.TryRemove(kvp.Key, out _);
                }
            }
        }

        public override string ToString()
        {
            return $"Tracker<{typeof(T).Name}>: {trackedObjects.Count} tracked objects";
        }
    }
    public partial class DBEngine
    {
        private readonly ConcurrentDictionary<Type, object> trackers = new ConcurrentDictionary<Type, object>();
        private readonly ConcurrentDictionary<Type,bool> untrackedobjects = new ConcurrentDictionary<Type, bool>();
        public bool DirtyAwareObjectCopy { get; set; } = true;
        public Tracker<T> GetTracker<T>() where T : class, new()
        {
            if (Tracked<T>.IsTrackable)
            {
                RichLogEntry logEntry = null;
                var tracker = (Tracker<T>)trackers.GetOrAdd(
                    typeof(T), t =>
                    {
                        logEntry = new RichLogEntry
                        {
                            Severity = 10,
                            Source = "Tracking",
                            Message = $"Tracking has been Initialized for {typeof(T).Name}"
                        };
                        return new Tracker<T>(this);
                    });
                if (DebugLevel >= 100 && logEntry != null) Log.Entry(logEntry);
                return tracker;
            }
            else
            {
                if (untrackedobjects.TryAdd(typeof(T),true))
                {
                    Log.Entry("Tracking",110,$"Tracking not available for {typeof(T).Name}",null);
                }
                return null;
            }
        }
        public bool TryGetTracker<T>(out Tracker<T> tracker) where T : class, new()
        {
            try
            {
                tracker = GetTracker<T>();
                return true;
            }
            catch (Exception)
            {
                tracker = null;
                return false;
            }
        }
    }
}
