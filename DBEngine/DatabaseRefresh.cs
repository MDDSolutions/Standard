using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MDDDataAccess
{
    // Result-shape information is supplied only by database materialization, never
    // inferred from a null incoming value or from GetOrAdd's initializing flag.
    internal static class DatabaseRefresh<T> where T : class
    {
        private static readonly PropertyInfo[] navigationProperties = GetNavigationProperties();

        private static PropertyInfo[] GetNavigationProperties()
        {
            var properties = typeof(T).GetProperties()
                .Where(p => p.IsDefined(typeof(DBInvalidateOnRefreshAttribute), true)).ToArray();
            foreach (var property in properties)
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0
                    || property.PropertyType.IsValueType || property.PropertyType == typeof(string))
                    throw new InvalidOperationException($"DBInvalidateOnRefresh requires a readable, writable navigation property: {typeof(T).Name}.{property.Name}.");
            return properties;
        }

        internal static void PrepareTarget(T target)
        {
            foreach (var property in navigationProperties)
                property.SetValue(target, null);
        }

        internal static void ClearUnmapped(T target, IList<PropertyMapEntry> map)
        {
            foreach (var property in navigationProperties)
                if (!map.Any(entry => entry.Property == property))
                    property.SetValue(target, null);
        }

        internal static void MergeNavigation(T target, T incoming, IList<PropertyMapEntry> map)
        {
            // Do this before CopyValues reads computed getters on the existing entity.
            // A mapped null is authoritative too (for example an empty LEFT JOIN).
            foreach (var property in navigationProperties)
                property.SetValue(target, map.Any(entry => entry.Property == property)
                    ? property.GetValue(incoming) : null);
        }
    }
}
