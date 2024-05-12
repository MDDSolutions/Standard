using MDDFoundation;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace MDDDataAccess
{
    public class ObjectTracker<T, TKey> : IObjectTracker where T : class, ITrackedEntity
    {
        private readonly ConcurrentDictionary<TKey, WeakReference<T>> trackedObjects = new ConcurrentDictionary<TKey, WeakReference<T>>();
        private readonly Func<T, TKey> keySelector;
        public ObjectTracker(Expression<Func<T, TKey>> keyPropertyExpression)
        {
            if (keyPropertyExpression.Body is MemberExpression memberExpression &&
                memberExpression.Member is PropertyInfo)
            {
                var property = (PropertyInfo)memberExpression.Member;
                keySelector = (Func<T, TKey>)property.GetGetMethod().CreateDelegate(typeof(Func<T, TKey>));
            }
            else
            {
                throw new ArgumentException("The expression must be a property selector", nameof(keyPropertyExpression));
            }
        }
        public T Load(T obj)
        {
            var key = keySelector(obj);
            var objWeakReference = new WeakReference<T>(obj);

            var finalref = trackedObjects.AddOrUpdate(key,
                objWeakReference,
                (k, existingRef) =>
                {
                    if (existingRef.TryGetTarget(out var existingObj))
                    {
                        var existingConcurrencyValue = GetListConcurrencyValue(existingObj);
                        var newConcurrencyValue = GetListConcurrencyValue(obj);

                        bool concurrencyequal;
                        if (existingConcurrencyValue is DateTime existingDate && newConcurrencyValue is DateTime newDate)
                        {
                            concurrencyequal = existingDate == newDate;
                        }
                        else if (existingConcurrencyValue is byte[] existingBytes && newConcurrencyValue is byte[] newBytes)
                        {
                            concurrencyequal = existingBytes.SequenceEqual(newBytes);
                        }
                        else
                        {
                            throw new InvalidOperationException("Unsupported concurrency value type.");
                        }

                        if (concurrencyequal)
                        {
                            return existingRef;
                        }
                        else if (existingObj.IsDirty)
                        {
                            var className = typeof(T).Name;
                            var dirtyProperties = existingObj.GetDirtyProperties(); // Assuming this method is available.
                            var dirtyPropertiesStr = string.Join(", ", dirtyProperties);

                            throw new InvalidOperationException(
                                $"Concurrency conflict detected on a dirty object of type {className} with key {key}. " +
                                $"Dirty properties: {dirtyPropertiesStr}");
                        }
                    }
                    else
                    {

                    }
                    return objWeakReference;
                });

            if (finalref.TryGetTarget(out var final) && ReferenceEquals(final, obj))
                obj.RaiseEntityUpdated(null, null, null);
            return final;
        }
        private PropertyInfo _listConcurrencyProperty;
        private object GetListConcurrencyValue(T obj)
        {
            if (_listConcurrencyProperty == null)
            {
                _listConcurrencyProperty = obj.GetType().GetProperties()
                    .FirstOrDefault(p => Attribute.IsDefined(p, typeof(ListConcurrencyAttribute)));
            }

            return _listConcurrencyProperty?.GetValue(obj);
        }
        public DateTime? GetLoadTime(T obj)
        {
            var key = keySelector(obj);
            return GetLoadTime(key);
        }
        public DateTime? GetLoadTime(TKey key)
        {
            if (trackedObjects.TryGetValue(key, out var weakRef) && weakRef.TryGetTarget(out var trackedObject))
            {
                return trackedObject.LoadedAt;
            }
            return (DateTime?)null;
        }
        public T GetObject(TKey key)
        {
            if (trackedObjects.TryGetValue(key, out var weakRef) && weakRef.TryGetTarget(out var trackedObject))
            {
                return trackedObject;
            }
            return default(T);
        }
        public object Load(object obj)
        {
            return Load((T)obj);
        }
        public void CleanupStaleEntries()
        {
            foreach (var key in trackedObjects.Keys)
            {
                if (trackedObjects.TryGetValue(key, out var weakRef) && !weakRef.TryGetTarget(out _))
                {
                    // Attempt to remove the item and get the removed value.
                    if (trackedObjects.TryRemove(key, out var removedref))
                    {
                        if (removedref.TryGetTarget(out var removedobj)) // It means the entry was replaced between our operations.
                        {
                            // Here you decide your strategy: re-insert or log a warning/error.
                            // For the sake of this example, we will re-insert the object.
                            if (trackedObjects.TryAdd(key, removedref))
                                Foundation.Log($"The impossible has happened - an ObjectTracker entry was re-inserted between the time CleanupStaleEntries identified it as stale and was able to remove it - it was re-inserted - hopefully no harm, no foul... - the ToString on the object is {removedobj}", false, DBEngine.LogFileName);
                            else
                                throw new AccessViolationException($"The impossible has happened - an ObjectTracker entry was re-inserted between the time CleanupStaleEntries identified it as stale and was able to remove it - it could not be re-inserted for some reason - the ToString on the object is {removedobj}");
                            // Alternatively, you could log an error or throw an exception, based on your needs.
                            // throw new InvalidOperationException($"Race condition detected for key {key}.");
                        }
                    }
                }
            }
        }
    }
    public interface IObjectTracker
    {
        object Load(object obj);
    }

}