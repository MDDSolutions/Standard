using MDDFoundation;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace MDDDataAccess
{
    public enum ObjectTracking
    {
        None,
        IfAvailable,
        Full
    }
    internal interface ITrackerMaintenance
    {
        void PruneInvalid();
    }

    public class Tracker<T> : ITrackerMaintenance where T : class, new()
    {
        public Tracker(DBEngine dbengine)
        {
            if (!TrackedEntity<T>.IsTrackable)
                throw new InvalidOperationException(
                    $"Type {typeof(T).FullName} is not trackable. A key is required; a concurrency token is optional but recommended."
                );
            DBEngine = dbengine;
        }
        public DBEngine DBEngine { get; }
        private readonly ConcurrentDictionary<object, TrackedEntity<T>> trackedObjects = new ConcurrentDictionary<object, TrackedEntity<T>>();
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
        public TrackedEntity<T> GetOrAdd(object key, object concurrency, out T entity)
        {
            if (key == null)
                throw new InvalidOperationException($"The key value provided for {typeof(T).FullName} was null");

            T lentity = null;

            var tracked = trackedObjects.AddOrUpdate(
                key,
                k => 
                {
                    lentity = new T();
                    return new TrackedEntity<T>(key, concurrency, lentity);
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
                            if (TrackedEntity<T>.HasConcurrency)
                            {
                                if (!Foundation.ValueEquals(concurrency, existing.ConcurrencyValue))
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
                            if (TrackedEntity<T>.HasConcurrency)
                            {
                                if (!Foundation.ValueEquals(concurrency, existing.ConcurrencyValue))
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
                            return new TrackedEntity<T>(key, concurrency, lentity);
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
        public TrackedEntity<T> GetOrAdd(ref T entity, bool initializing = false)
        {
            var key = TrackedEntity<T>.GetKeyValue(entity);
            if (key == null)
                throw new InvalidOperationException($"The entity provided for {typeof(T).FullName} has a null key value");

            T loading = entity;
            var tracked = trackedObjects.AddOrUpdate(
                key, 
                k => new TrackedEntity<T>(loading),
                (k, existingtracked) =>
                {
                    if (existingtracked.TryGetEntity(out T existingentity))
                    {
                        if (ReferenceEquals(existingentity, loading))
                        {
                            if (initializing)
                            {
                                try
                                {
                                    existingtracked.Initializing = true;
                                }
                                finally
                                {
                                    existingtracked.EndInitialization();
                                }
                            }
                            if (existingtracked.State == TrackedState.Initializing)
                            {
                                //this should never happen - we could throw here, but the entity may be valid - we could just
                                //end initialization and hope for the best
                                //if we throw, we would need to somehow send the user back to the database to reload
                                //existingtracked.EndInitialization();
                                throw new InvalidOperationException($"There is an object in the {typeof(T).Name} tracker with key {key} in an initializing state - this really shouldn't happen");
                            }
                            return existingtracked;
                        }
                        else
                        {
                            var loadingconcurrency = TrackedEntity<T>.GetConcurrencyValue?.Invoke(loading);
                            switch (existingtracked.State)
                            {
                                case TrackedState.Unchanged:
                                    //object exists and is unchanged in the tracker - if the concurrency property is the same, our load was for naught - we can just return the existing value
                                    //if the object does not have concurrency, I guess we just blindly merge the values from the loaded object - they could be new, but who knows?
                                    //if (DebugLevel >= 200) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 55, "OFR cache hit", r.ToString(), typeof(T).Name));

                                    if (!TrackedEntity<T>.HasConcurrency || !Foundation.ValueEquals(loadingconcurrency, TrackedEntity<T>.GetConcurrencyValue(existingentity)))
                                    {
                                        var allowDirtyAware = DBEngine.DirtyAwareObjectCopy && TrackedEntity<T>.SupportsDirtyAwareCopy;
                                        existingtracked.CopyValues(loading, allowDirtyAware);
                                    }
                                    else
                                    {
                                        existingtracked.CopyValues(loading, true, true);
                                    }
                                    loading = existingentity;
                                    return existingtracked;
                                case TrackedState.Modified:
                                    //if the object exists and is modified, then we don't want any values from the new object whether it's concurrency value matches or not
                                    //if the concurrency value does not match, then we are probably previewing a future concurrency conflict but not sure what to do about it
                                    //now - whoever has the object and hasn't saved it yet is going to get an error, but there is no mechanism here to tell them
                                    //whoever is loading the object is going to get the dirty, unsaved one and not the version in the database - that might be a clue
                                    //if (DebugLevel >= 200) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 55, "OFR cache hit", r.ToString(), typeof(T).Name));
                                    var attemptDirtyAware = DBEngine.DirtyAwareObjectCopy && TrackedEntity<T>.SupportsDirtyAwareCopy;
                                    existingtracked.CopyValues(loading, attemptDirtyAware);
                                    loading = existingentity;
                                    return existingtracked;
                                case TrackedState.Initializing:
                                    throw new InvalidOperationException($"There is an object in the {typeof(T).Name} tracker with key {key} in an initializing state - this really shouldn't happen");
                                case TrackedState.Invalid:
                                default:
                                    return new TrackedEntity<T>(loading);
                            }
                        }
                    }
                    return new TrackedEntity<T>(loading);
                });
            entity = loading;
            return tracked;
        }

        public bool TryGet(object key, out TrackedEntity<T> tracked) => trackedObjects.TryGetValue(key, out tracked);
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
        public ObjectTracking Tracking { get; set; } = ObjectTracking.None;

        private readonly ConcurrentDictionary<Type, object> trackers = new ConcurrentDictionary<Type, object>();
        private readonly object trackerMaintenanceSync = new object();
        private CancellationTokenSource trackerMaintenanceCancellation;
        private Thread trackerMaintenanceThread;
        private bool trackerMaintenanceShutdownHooked = false;

        public TimeSpan TrackerMaintenanceInterval { get; set; } = TimeSpan.FromMinutes(5);
        private readonly ConcurrentDictionary<Type,bool> untrackedobjects = new ConcurrentDictionary<Type, bool>();
        public bool DirtyAwareObjectCopy { get; set; } = false;
        public Tracker<T> GetTracker<T>() where T : class, new()
        {
            if (TrackedEntity<T>.IsTrackable)
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

                EnsureTrackerMaintenanceRunning();

                if (string.IsNullOrWhiteSpace(TrackedEntity<T>.UpdateCommand))
                {
                    string procname = $"{typeof(T).Name}_Upsert";
                    var testforparams = ProcedureParameterList(procname);
                    if (testforparams.Count > 0)
                        TrackedEntity<T>.UpdateCommand = procname;
                }


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
        private void EnsureTrackerMaintenanceRunning()
        {
            if (trackers.IsEmpty)
                return;

            lock (trackerMaintenanceSync)
            {
                if (trackerMaintenanceThread != null && trackerMaintenanceThread.IsAlive)
                    return;

                trackerMaintenanceCancellation?.Dispose();
                trackerMaintenanceCancellation = new CancellationTokenSource();
                var token = trackerMaintenanceCancellation.Token;

                trackerMaintenanceThread = new Thread(() => TrackerMaintenanceLoop(token))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Lowest,
                    Name = $"DBEngineTrackerMaintenance-{GetHashCode()}"
                };

                trackerMaintenanceThread.Start();

                if (!trackerMaintenanceShutdownHooked)
                {
                    AppDomain.CurrentDomain.ProcessExit += (_, __) => ShutdownTrackerMaintenance();
                    AppDomain.CurrentDomain.DomainUnload += (_, __) => ShutdownTrackerMaintenance();
                    trackerMaintenanceShutdownHooked = true;
                }
            }
        }

        private void TrackerMaintenanceLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    foreach (var tracker in trackers.Values.OfType<ITrackerMaintenance>())
                    {
                        if (token.IsCancellationRequested)
                            break;

                        try
                        {
                            tracker.PruneInvalid();
                        }
                        catch (Exception ex)
                        {
                            LogTrackerMaintenanceError($"Tracker prune failure in {tracker.GetType().Name}", ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogTrackerMaintenanceError("Tracker maintenance loop failure", ex);
                }

                if (token.IsCancellationRequested)
                    break;

                var delay = TrackerMaintenanceInterval;
                if (delay <= TimeSpan.Zero)
                    delay = TimeSpan.FromSeconds(1);

                try
                {
                    if (token.WaitHandle.WaitOne(delay))
                        break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        private void LogTrackerMaintenanceError(string message, Exception ex)
        {
            try
            {
                Log?.Entry("DBEngine.Tracking", 90, message, ex?.ToString());
            }
            catch
            {
                // ignored
            }
        }

        public void ShutdownTrackerMaintenance()
        {
            Thread threadToJoin = null;

            lock (trackerMaintenanceSync)
            {
                if (trackerMaintenanceCancellation == null)
                    return;

                try
                {
                    trackerMaintenanceCancellation.Cancel();
                }
                catch
                {
                    // ignored
                }

                threadToJoin = trackerMaintenanceThread;
                trackerMaintenanceThread = null;
            }

            if (threadToJoin != null)
            {
                try
                {
                    if (threadToJoin.IsAlive)
                        threadToJoin.Join(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // ignored
                }
            }

            lock (trackerMaintenanceSync)
            {
                trackerMaintenanceCancellation?.Dispose();
                trackerMaintenanceCancellation = null;
            }
        }
        public object TrackerAddObject(object entity, bool initializing)
        {
            var getTrackerMethod = typeof(DBEngine).GetMethod("GetTracker", BindingFlags.Public | BindingFlags.Instance);
            var genericMethod = getTrackerMethod.MakeGenericMethod(entity.GetType());
            var ctracker = genericMethod.Invoke(this, null);

            var getOrAddMethod = ctracker.GetType().GetMethod("GetOrAdd", new[] { entity.GetType().MakeByRefType(), typeof(bool) });
            var parameters = new object[] { entity, initializing };
            var tracked = getOrAddMethod.Invoke(ctracker, parameters);
            entity = parameters[0]; // update ref parameter if needed
            return tracked;
        }
    }
}
