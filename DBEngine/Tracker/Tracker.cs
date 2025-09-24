using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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
            var concurrency = Tracked<T>.GetConcurrencyValue?.Invoke(entity);

            T lentity = entity;

            var tracked = trackedObjects.AddOrUpdate(
                key, 
                k => new Tracked<T>(lentity),
                (k, existing) =>
                {
                    /* The whole point here is that the entity has been fully initialized outside of the tracker, and so should not exist
                     * ...if it does, we'll have to do some stuff to reconcile the two instances.
                     * ...not doing that right now...
                     */
                    T e;
                    if (existing.TryGetEntity(out e))
                    {
                        if (ReferenceEquals(e, lentity))
                            //same instance - nothing to do
                            return existing;
                        else
                            //different instances - not sure what to do here yet
                            throw new NotImplementedException("An entity with the same key is already being tracked - reconciling two different instances is not implemented.");
                    }
                    return new Tracked<T>(lentity);
                });
            entity = lentity;
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
        public Tracker<T> GetTracker<T>() where T : class, new()
        {
            var tracker = (Tracker<T>)trackers.GetOrAdd(typeof(T), t => new Tracker<T>(this));
            return tracker;
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
