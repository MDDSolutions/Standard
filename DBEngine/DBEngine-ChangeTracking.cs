using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace MDDDataAccess
{
    public enum ObjectTracking
    {
        None,
        IfAvailable,
        Full
    }
    public partial class DBEngine
    {
        public ObjectTracking Tracking { get; set; } = ObjectTracking.None;
        private ConcurrentDictionary<Type, object> objectTrackers = new ConcurrentDictionary<Type, object>();
        private ObjectTracker<T, TKey> GetOrCreateTrackerGeneric<T, TKey>() where T : class, ITrackedEntity
        {
            var type = typeof(T);

            var trackerObject = objectTrackers.GetOrAdd(type, t =>
            {
                var keyProperty = type.GetProperties().FirstOrDefault(x => x.GetCustomAttributes(typeof(ListKeyAttribute), true) != null);

                if (keyProperty == null)
                    throw new InvalidOperationException($"Type {t.Name} does not have a ListKey attribute.");

                //var entityParam = Expression.Parameter(type, "x");
                //var propertyAccess = Expression.Property(entityParam, keyProperty);
                //var keySelector = Expression.Lambda<Func<T, TKey>>(propertyAccess, entityParam);

                var keySelector = (Expression<Func<T, TKey>>)Expression.Lambda(
                        typeof(Func<T, TKey>),
                        Expression.Property(Expression.Parameter(type, "x"), keyProperty),
                        Expression.Parameter(type, "x"));

                return new ObjectTracker<T, TKey>(keySelector);
            });

            return (ObjectTracker<T, TKey>)trackerObject;
        }
        public object GetOrCreateTracker<T>()
        {
            var type = typeof(T);
            var keyType = type.GetProperties().FirstOrDefault(x => x.GetCustomAttributes(typeof(ListKeyAttribute), true) != null);

            var method = typeof(DBEngine).GetMethod("GetOrCreateTrackerGeneric", BindingFlags.NonPublic | BindingFlags.Instance);
            var genericMethod = method.MakeGenericMethod(type, keyType.PropertyType);

            return genericMethod.Invoke(this, null);
        }
    }
}
