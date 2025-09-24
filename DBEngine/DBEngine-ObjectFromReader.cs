using MDDFoundation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

namespace MDDDataAccess
{
    public partial class DBEngine
    {
        public bool EnumParseIgnoreCase { get; set; } = true;
        public void ObjectFromReader<T>(SqlDataReader rdr, ref List<PropertyMapEntry> map, ref PropertyInfo key, ref T r, ref Tracker<T> tracker, bool strict = true) where T : class, new()
        {
            var concurrencyproperty = PrepareTracking(rdr, ref key, ref r, ref tracker, out var tracked, out var skipLoad);
            if (skipLoad) return;

            concurrencyproperty = EnsureConcurrencyProperty(r, strict, concurrencyproperty);

            var descriptors = GetPropertyDescriptors(r, ref key, strict, concurrencyproperty);

            bool hasMap = BuildPropertyMapIfNeeded(rdr, ref map, descriptors, r);

            if (hasMap && map != null && map.Count > 0)
            {
                ExecutePropertyMap(rdr, map, r);
            }
            else
            {
                ExecuteSequentialNoMap(rdr, descriptors, r);
            }

            FinalizeTracking(tracked, r);
        }
        private PropertyInfo PrepareTracking<T>(SqlDataReader rdr, ref PropertyInfo key, ref T r, ref Tracker<T> tracker, out Tracked<T> tracked, out bool skipLoad) where T : class, new()
        {
            tracked = null;
            skipLoad = false;
            PropertyInfo concurrencyproperty = null;

            if (tracker != null)
            {
                if (Tracked<T>.HasConcurrency)
                    concurrencyproperty = Tracked<T>.ConcurrencyProperty;

                if (r != null)
                {
                    var keyvalue = Tracked<T>.GetKeyValue(r);
                    if (!IsDefaultOrNull(keyvalue) && tracker.TryGet(keyvalue, out tracked))
                    {
                        switch (tracked.State)
                        {
                            case TrackedState.Unchanged:
                            case TrackedState.Modified:
                                if (tracked.TryGetEntity(out var existing) && !ReferenceEquals(existing, r))
                                    throw new Exception($"DBEngine.ObjectFromReader ERROR: Tracking has been set to {Tracking} but an object of type {typeof(T).Name} with a key value of {keyvalue} is being (re)loaded but is somehow not the same object as what has been passed to OFR");
                                tracked.Initializing = true;
                                if (DebugLevel >= 200) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 55, "OFR cache hit", r.ToString(), typeof(T).Name));
                                break;
                            case TrackedState.Initializing:
                                throw new Exception($"DBEngine.ObjectFromReader ERROR: Tracking has been set to {Tracking} but an object of type {typeof(T).Name} with a key value of {keyvalue} is being (re)loaded and is already in the Initializing state");
                            case TrackedState.Invalid:
                            default:
                                throw new Exception($"DBEngine.ObjectFromReader ERROR: Tracking has been set to {Tracking} but an object of type {typeof(T).Name} with a key value of {keyvalue} is being (re)loaded and is in an invalid state");
                        }
                    }
                    else
                    {
                        throw new Exception($"DBEngine.ObjectFromReader ERROR: Tracking has been set to {Tracking} but an object of type {typeof(T).Name} with a key value of {keyvalue} is being (re)loaded and is not in the tracker");
                    }
                }
                else
                {
                    var keyvalue = rdr[Tracked<T>.KeyDBName];
                    var rdrconcurrency = Tracked<T>.HasConcurrency ? rdr[Tracked<T>.ConcurrencyDBName] : null;
                    if (tracker.TryGet(keyvalue, out tracked))
                    {
                        var curstate = tracked.State;
                        switch (curstate)
                        {
                            case TrackedState.Modified:
                            case TrackedState.Unchanged:
                                if (tracked.TryGetEntity(out var existing))
                                {
                                    r = existing;
                                    var entityconcurrency = Tracked<T>.GetConcurrencyValue?.Invoke(r);
                                    if (Tracked<T>.HasConcurrency && ValueEquals(rdrconcurrency, entityconcurrency))
                                    {
                                        if (DebugLevel >= 200) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 56, "OFR cache hit - concurrency match", r.ToString(), typeof(T).Name));
                                        TrackerHitCount++;
                                        skipLoad = true;
                                        return concurrencyproperty;
                                    }
                                    else if (curstate == TrackedState.Unchanged)
                                    {
                                        tracked.Initializing = true;
                                        if (DebugLevel >= 200) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 53, "OFR cache update", r.ToString(), typeof(T).Name));
                                    }
                                    else
                                    {
                                        throw new Exception($"DBEngine.ObjectFromReader ERROR: Tracking has been set to {Tracking} but an object of type {typeof(T).Name} with a key value of {keyvalue} is being loaded and is dirty but the concurrency value in the database does not match the concurrency value of the object - you must either set Tracking to None or ensure that all objects being loaded are not already being tracked as dirty");
                                    }
                                }
                                break;
                            case TrackedState.Initializing:
                                throw new Exception($"DBEngine.ObjectFromReader ERROR: Tracking has been set to {Tracking} but an object of type {typeof(T).Name} with a key value of {keyvalue} is being loaded and is already in the Initializing state");
                            case TrackedState.Invalid:
                            default:
                                tracked.BeginInitialization(keyvalue, rdrconcurrency, out r);
                                if (DebugLevel >= 200) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 51, "OFR revive", $"Type: {typeof(T).Name} ID: {keyvalue}", typeof(T).Name));
                                break;
                        }
                    }
                    else
                    {
                        tracked = tracker.GetOrAdd(keyvalue, rdrconcurrency, out r);
                        if (DebugLevel >= 220) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 51, "OFR create", $"Type: {typeof(T).Name} ID: {keyvalue}", typeof(T).Name));
                    }
                }
            }
            else
            {
                if (r == null) r = new T();
                if (Tracked<T>.HasConcurrency)
                    concurrencyproperty = Tracked<T>.ConcurrencyProperty;
            }

            return concurrencyproperty;
        }

        private PropertyInfo EnsureConcurrencyProperty<T>(T target, bool strict, PropertyInfo concurrencyproperty) where T : class
        {
            if (!strict)
            {
                var keyinfo = AttributeInfo(target, typeof(ListKeyAttribute));
                if (keyinfo == null)
                    keyinfo = AttributeInfo(target, typeof(ListKeyAttribute));
                if (keyinfo.Item1 == null || !keyinfo.Item2)
                    throw new Exception($"DBEngine error: Non-strict ObjectFromReader calls require that the object being loaded have a property marked with ListKeyAttribute and that the property have a value");

                if (concurrencyproperty == null)
                    concurrencyproperty = AttributeProperty<T>(typeof(ListConcurrencyAttribute));
                if (concurrencyproperty == null)
                    throw new Exception($"DBEngine error: Non-strict ObjectFromReader calls require that the object being loaded have a property marked with ListConcurrencyAttribute");
            }

            return concurrencyproperty;
        }

        private List<PropertyDescriptor> GetPropertyDescriptors<T>(T target, ref PropertyInfo key, bool strict, PropertyInfo concurrencyproperty) where T : class
        {
            var descriptors = new List<PropertyDescriptor>();

            foreach (var item in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                bool include = true;
                bool optional = false;
                string dbName = item.Name;

                foreach (var attr in item.GetCustomAttributes(true))
                {
                    if (attr is DBIgnoreAttribute)
                        include = false;
                    if (attr is DBNameAttribute dbna)
                        dbName = dbna.DBName;
                    if (attr is DBOptionalAttribute)
                        optional = true;
                    if (attr is ListKeyAttribute)
                        key = item;
                    if (attr is DBLoadedTimeAttribute)
                    {
                        include = false;
                        item.SetValue(target, DateTime.Now);
                    }
                }

                if (!include || !item.CanWrite)
                    continue;

                if (!strict)
                    optional = item != concurrencyproperty;

                var descriptor = new PropertyDescriptor
                {
                    Property = item,
                    ColumnName = dbName,
                    Optional = optional,
                    Handler = CreatePropertyHandler(item),
                    ForcedOrdinal = target is StringObj ? 0 : (int?)null
                };

                descriptors.Add(descriptor);
            }

            return descriptors;
        }

        private bool BuildPropertyMapIfNeeded<T>(SqlDataReader rdr, ref List<PropertyMapEntry> map, List<PropertyDescriptor> descriptors, T target) where T : class
        {
            if (map != null && map.Count > 0)
                return true;

            if (descriptors == null || descriptors.Count == 0)
            {
                map = null;
                return false;
            }

            var entries = new List<PropertyMapEntry>();

            foreach (var descriptor in descriptors)
            {
                if (descriptor.Handler == null)
                    continue;

                int ordinal;
                if (descriptor.ForcedOrdinal.HasValue)
                {
                    ordinal = descriptor.ForcedOrdinal.Value;
                    if (ordinal >= rdr.FieldCount)
                    {
                        if (!descriptor.Optional)
                            throw new Exception($"DBEngine internal error: The column '{descriptor.ColumnName ?? descriptor.Property.Name}' was specified as a property (or DBName attribute) in the '{target.GetType().Name}' object but was not found in a query meant to populate objects of that type - you must either decorate this property with a DBIgnore attribute if you want ObjectFromReader to always ignore it, or a DBOptional attribute if you want ObjectFromReader to use it if it is there, but ignore it if it is not");
                        else
                            continue;
                    }
                }
                else
                {
                    if (descriptor.Optional && !ColumnExists(rdr, descriptor.ColumnName))
                        continue;

                    try
                    {
                        ordinal = rdr.GetOrdinal(descriptor.ColumnName);
                    }
                    catch (IndexOutOfRangeException)
                    {
                        if (descriptor.Optional)
                            continue;
                        throw new Exception($"DBEngine internal error: The column '{descriptor.ColumnName ?? descriptor.Property.Name}' was specified as a property (or DBName attribute) in the '{target.GetType().Name}' object but was not found in a query meant to populate objects of that type - you must either decorate this property with a DBIgnore attribute if you want ObjectFromReader to always ignore it, or a DBOptional attribute if you want ObjectFromReader to use it if it is there, but ignore it if it is not");
                    }
                }

                var handler = descriptor.Handler;
                var readerFunc = handler.ReaderFunc ?? ((Func<SqlDataReader, int, object>)((reader, ord) => reader.IsDBNull(ord) ? null : reader.GetValue(ord)));
                var mapAction = new Action<SqlDataReader, object>((reader, targetObj) => handler.Assign(reader, ordinal, targetObj));
                string readerTypeName;
                try
                {
                    readerTypeName = rdr.GetFieldType(ordinal).FullName;
                }
                catch
                {
                    readerTypeName = "<unknown>";
                }

                entries.Add(new PropertyMapEntry
                {
                    Ordinal = ordinal,
                    ReaderFunc = readerFunc,
                    MapAction = mapAction,
                    PropertyName = descriptor.Property.Name,
                    PropertyTypeName = descriptor.Property.PropertyType.FullName,
                    ReaderTypeName = readerTypeName
                });
            }

            if (entries.Count == 0)
            {
                map = null;
                return false;
            }

            entries.Sort((x, y) => x.Ordinal.CompareTo(y.Ordinal));
            map = entries;
            return true;
        }

        private void ExecutePropertyMap<T>(SqlDataReader rdr, List<PropertyMapEntry> map, T target) where T : class
        {
            foreach (var entry in map)
            {
                try
                {
                    entry.MapAction(rdr, target);
                }
                catch (Exception ex)
                {
                    object valueForError;
                    try
                    {
                        valueForError = entry.ReaderFunc(rdr, entry.Ordinal);
                    }
                    catch
                    {
                        valueForError = "<error reading value>";
                    }

                    string objectvalues = string.Join(", ", typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(pi => $"{pi.Name}={(pi.GetValue(target) == null ? "<null>" : pi.GetValue(target).ToString())}"));

                    throw new Exception($"DBEngine internal error: Post-mapping error occurred trying to set {target.GetType().Name}.{entry.PropertyName} to {valueForError} the property is an {entry.PropertyTypeName} and the mapper clocked the reader as an {entry.ReaderTypeName} (the data types must match exactly) - see inner exception - values in object: {objectvalues}", ex);
                }
            }
        }

        private void ExecuteSequentialNoMap<T>(SqlDataReader rdr, List<PropertyDescriptor> descriptors, T target) where T : class
        {
            if (descriptors == null || descriptors.Count == 0)
                return;

            var columnDescriptors = new Dictionary<string, PropertyDescriptor>(StringComparer.InvariantCultureIgnoreCase);
            var ordinalDescriptors = new Dictionary<int, List<PropertyDescriptor>>();

            foreach (var descriptor in descriptors)
            {
                if (descriptor.Handler == null)
                    continue;

                if (descriptor.ForcedOrdinal.HasValue)
                {
                    if (!ordinalDescriptors.TryGetValue(descriptor.ForcedOrdinal.Value, out var list))
                    {
                        list = new List<PropertyDescriptor>();
                        ordinalDescriptors[descriptor.ForcedOrdinal.Value] = list;
                    }
                    list.Add(descriptor);
                }
                else if (!string.IsNullOrEmpty(descriptor.ColumnName) && !columnDescriptors.ContainsKey(descriptor.ColumnName))
                {
                    columnDescriptors.Add(descriptor.ColumnName, descriptor);
                }
            }

            foreach (var descriptor in descriptors.Where(d => !d.Optional))
            {
                if (descriptor.ForcedOrdinal.HasValue)
                {
                    if (descriptor.ForcedOrdinal.Value >= rdr.FieldCount)
                        throw new Exception($"DBEngine internal error: The column '{descriptor.ColumnName ?? descriptor.Property.Name}' was specified as a property (or DBName attribute) in the '{target.GetType().Name}' object but was not found in a query meant to populate objects of that type - you must either decorate this property with a DBIgnore attribute if you want ObjectFromReader to always ignore it, or a DBOptional attribute if you want ObjectFromReader to use it if it is there, but ignore it if it is not");
                }
                else if (!ColumnExists(rdr, descriptor.ColumnName))
                {
                    throw new Exception($"DBEngine internal error: The column '{descriptor.ColumnName ?? descriptor.Property.Name}' was specified as a property (or DBName attribute) in the '{target.GetType().Name}' object but was not found in a query meant to populate objects of that type - you must either decorate this property with a DBIgnore attribute if you want ObjectFromReader to always ignore it, or a DBOptional attribute if you want ObjectFromReader to use it if it is there, but ignore it if it is not");
                }
            }

            for (int ordinal = 0; ordinal < rdr.FieldCount; ordinal++)
            {
                if (ordinalDescriptors.TryGetValue(ordinal, out var ordinalList))
                {
                    foreach (var descriptor in ordinalList)
                    {
                        descriptor.Handler.Assign(rdr, ordinal, target);
                    }
                    continue;
                }

                var columnName = rdr.GetName(ordinal);
                if (columnDescriptors.TryGetValue(columnName, out var descriptorForColumn))
                {
                    descriptorForColumn.Handler.Assign(rdr, ordinal, target);
                }
            }
        }

        private void FinalizeTracking<T>(Tracked<T> tracked, T target) where T : class
        {
            if (tracked == null)
                return;

            if (!tracked.Initializing)
                throw new Exception($"DBEngine.ObjectFromReader ERROR: Tracking has been set to {Tracking} but an object of type {typeof(T).Name} with a key value of {Tracked<T>.GetKeyValue(target)} has completed loading but was not in the Initializing state");

            tracked.EndInitialization();
        }

        private static bool ColumnExists(SqlDataReader rdr, string columnName)
        {
            if (string.IsNullOrEmpty(columnName))
                return false;

            for (int i = 0; i < rdr.FieldCount; i++)
            {
                if (rdr.GetName(i).Equals(columnName, StringComparison.InvariantCultureIgnoreCase))
                    return true;
            }

            return false;
        }

        private PropertyHandler CreatePropertyHandler(PropertyInfo property)
        {
            var setter = BuildCompiledSetter(property);
            var propertyType = property.PropertyType;
            var underlyingNullable = Nullable.GetUnderlyingType(propertyType);

            if (propertyType == typeof(char) || underlyingNullable == typeof(char))
            {
                bool isNullable = underlyingNullable == typeof(char);
                return new PropertyHandler
                {
                    ReaderFunc = (reader, ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal),
                    Assign = (reader, ordinal, target) =>
                    {
                        if (reader.IsDBNull(ordinal))
                        {
                            if (isNullable)
                                setter(target, null);
                            else
                                setter(target, default(char));
                            return;
                        }

                        var value = reader.GetValue(ordinal);
                        char charValue;
                        if (value is string s && !string.IsNullOrEmpty(s))
                            charValue = s[0];
                        else if (value is char c)
                            charValue = c;
                        else
                            charValue = Convert.ToChar(value);

                        if (isNullable)
                            setter(target, (char?)charValue);
                        else
                            setter(target, charValue);
                    }
                };
            }

            var enumType = propertyType.IsEnum ? propertyType : (underlyingNullable != null && underlyingNullable.IsEnum ? underlyingNullable : null);
            if (enumType != null)
            {
                bool isNullable = underlyingNullable != null && underlyingNullable.IsEnum;
                return new PropertyHandler
                {
                    ReaderFunc = (reader, ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal),
                    Assign = (reader, ordinal, target) =>
                    {
                        if (reader.IsDBNull(ordinal))
                        {
                            if (isNullable)
                                setter(target, null);
                            else
                                setter(target, Activator.CreateInstance(enumType));
                            return;
                        }

                        var raw = reader.GetValue(ordinal);
                        object enumValue;
                        if (raw is int || raw is short || raw is long || raw is byte)
                            enumValue = Enum.ToObject(enumType, raw);
                        else
                            enumValue = Enum.Parse(enumType, raw.ToString(), EnumParseIgnoreCase);

                        setter(target, enumValue);
                    }
                };
            }

            if (typeof(ISerializable).IsAssignableFrom(propertyType) && !propertyType.FullName.Contains("System"))
            {
                return new PropertyHandler
                {
                    ReaderFunc = (reader, ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal),
                    Assign = (reader, ordinal, target) =>
                    {
                        if (reader.IsDBNull(ordinal))
                        {
                            setter(target, GetDefaultValue(propertyType));
                            return;
                        }

                        var buffer = reader.GetValue(ordinal) as byte[];
                        if (buffer != null)
                        {
                            var formatter = new BinaryFormatter();
                            using (var stream = new MemoryStream(buffer))
                            {
                                var obj = formatter.Deserialize(stream);
                                setter(target, obj);
                            }
                        }
                        else
                        {
                            setter(target, GetDefaultValue(propertyType));
                        }
                    }
                };
            }

            var readerFunc = GetReaderFunc(propertyType) ?? ((Func<SqlDataReader, int, object>)((reader, ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal)));

            return new PropertyHandler
            {
                ReaderFunc = readerFunc,
                Assign = (reader, ordinal, target) =>
                {
                    var value = readerFunc(reader, ordinal);
                    setter(target, value);
                }
            };
        }

        private static object GetDefaultValue(Type type)
        {
            if (type.IsValueType)
                return Activator.CreateInstance(type);
            return null;
        }

        private class PropertyDescriptor
        {
            public PropertyInfo Property { get; set; }
            public string ColumnName { get; set; }
            public bool Optional { get; set; }
            public PropertyHandler Handler { get; set; }
            public int? ForcedOrdinal { get; set; }
        }

        private class PropertyHandler
        {
            public Action<SqlDataReader, int, object> Assign { get; set; }
            public Func<SqlDataReader, int, object> ReaderFunc { get; set; }
        }

        public class PropertyMapEntry
        {
            public int Ordinal;
            public Action<object, object> Setter;
            public Func<SqlDataReader, int, object> ReaderFunc;
            public Action<SqlDataReader, object> MapAction; // alternative to Setter + ReaderFunc
            public string PropertyName { get; set; }
            public string  PropertyTypeName { get; set; }
            public string ReaderTypeName { get; set; }
        }
        // Helper to build compiled setter
        private static Action<object, object> BuildCompiledSetter(PropertyInfo property)
        {
            var targetType = property.DeclaringType;
            var valueType = property.PropertyType;

            var targetExp = Expression.Parameter(typeof(object), "target");
            var valueExp = Expression.Parameter(typeof(object), "value");

            var castTargetExp = Expression.Convert(targetExp, targetType);
            var castValueExp = Expression.Convert(valueExp, valueType);

            var propertyExp = Expression.Property(castTargetExp, property);
            var assignExp = Expression.Assign(propertyExp, castValueExp);

            var lambda = Expression.Lambda<Action<object, object>>(assignExp, targetExp, valueExp);
            return lambda.Compile();
        }

        // Helper to get type-specific reader
        private static Func<SqlDataReader, int, object> GetReaderFunc(Type type)
        {
            // Nullable types
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                var underlyingType = Nullable.GetUnderlyingType(type);
                if (underlyingType == typeof(int))
                    return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (int?)null : rdr.GetInt32(ordinal);
                if (underlyingType == typeof(long))
                    return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (long?)null : rdr.GetInt64(ordinal);
                if (underlyingType == typeof(short))
                    return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (short?)null : rdr.GetInt16(ordinal);
                if (underlyingType == typeof(byte))
                    return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (byte?)null : rdr.GetByte(ordinal);
                if (underlyingType == typeof(bool))
                    return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (bool?)null : rdr.GetBoolean(ordinal);
                if (underlyingType == typeof(float))
                    return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (float?)null : rdr.GetFloat(ordinal);
                if (underlyingType == typeof(double))
                    return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (double?)null : rdr.GetDouble(ordinal);
                if (underlyingType == typeof(decimal))
                    return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (decimal?)null : rdr.GetDecimal(ordinal);
                if (underlyingType == typeof(Guid))
                    return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (Guid?)null : rdr.GetGuid(ordinal);
                if (underlyingType == typeof(DateTime))
                    return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (DateTime?)null : rdr.GetDateTime(ordinal);
                if (underlyingType == typeof(DateTimeOffset))
                    return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (DateTimeOffset?)null : rdr.GetFieldValue<DateTimeOffset>(ordinal);
                // Add more nullable types as needed
            }

            // Non-nullable types
            if (type == typeof(int))
                return (rdr, ordinal) => rdr.GetInt32(ordinal);
            if (type == typeof(long))
                return (rdr, ordinal) => rdr.GetInt64(ordinal);
            if (type == typeof(short))
                return (rdr, ordinal) => rdr.GetInt16(ordinal);
            if (type == typeof(byte))
                return (rdr, ordinal) => rdr.GetByte(ordinal);
            if (type == typeof(bool))
                return (rdr, ordinal) => rdr.GetBoolean(ordinal);
            if (type == typeof(float))
                return (rdr, ordinal) => rdr.GetFloat(ordinal);
            if (type == typeof(double))
                return (rdr, ordinal) => rdr.GetDouble(ordinal);
            if (type == typeof(decimal))
                return (rdr, ordinal) => rdr.GetDecimal(ordinal);
            if (type == typeof(Guid))
                return (rdr, ordinal) => rdr.GetGuid(ordinal);
            if (type == typeof(DateTime))
                return (rdr, ordinal) => rdr.GetDateTime(ordinal);
            if (type == typeof(DateTimeOffset))
                return (rdr, ordinal) => rdr.GetFieldValue<DateTimeOffset>(ordinal);

            // Reference types
            if (type == typeof(string))
                return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? null : rdr.GetString(ordinal);
            if (type == typeof(byte[]))
                return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? null : (byte[])rdr.GetValue(ordinal);

            // Fallback for other types
            return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? null : rdr.GetValue(ordinal);
        }
        private static Action<SqlDataReader, object> BuildCompiledMap(PropertyInfo property, int ordinal)
        {
            var targetType = property.DeclaringType;
            var propertyType = property.PropertyType;

            var readerParam = Expression.Parameter(typeof(SqlDataReader), "rdr");
            var targetParam = Expression.Parameter(typeof(object), "target");

            // Cast target to correct type
            var castTarget = Expression.Convert(targetParam, targetType);

            // Build reader logic
            Expression valueExp;
            MethodInfo isDbNullMethod = typeof(SqlDataReader).GetMethod("IsDBNull");
            var ordinalExp = Expression.Constant(ordinal);

            if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                var underlyingType = Nullable.GetUnderlyingType(propertyType);
                var getMethod = typeof(SqlDataReader).GetMethod("Get" + underlyingType.Name, new[] { typeof(int) });
                var nullValue = Expression.Constant(null, propertyType);

                valueExp = Expression.Condition(
                    Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                    nullValue,
                    Expression.Convert(Expression.Call(readerParam, getMethod, ordinalExp), propertyType)
                );
            }
            else if (propertyType == typeof(string))
            {
                var getStringMethod = typeof(SqlDataReader).GetMethod("GetString");
                valueExp = Expression.Condition(
                    Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                    Expression.Constant(null, typeof(string)),
                    Expression.Call(readerParam, getStringMethod, ordinalExp)
                );
            }
            else if (propertyType == typeof(byte[]))
            {
                valueExp = Expression.Condition(
                    Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                    Expression.Constant(null, typeof(byte[])),
                    Expression.Convert(Expression.Call(readerParam, typeof(SqlDataReader).GetMethod("GetValue"), ordinalExp), typeof(byte[]))
                );
            }
            else if (propertyType.IsValueType)
            {
                var getMethod = typeof(SqlDataReader).GetMethod("Get" + propertyType.Name, new[] { typeof(int) });
                valueExp = Expression.Call(readerParam, getMethod, ordinalExp);
            }
            else
            {
                // Fallback: object
                valueExp = Expression.Condition(
                    Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                    Expression.Constant(null),
                    Expression.Call(readerParam, typeof(SqlDataReader).GetMethod("GetValue"), ordinalExp)
                );
            }

            // Set property
            var setMethod = property.GetSetMethod(true);
            var setExp = Expression.Call(castTarget, setMethod, valueExp);

            var lambda = Expression.Lambda<Action<SqlDataReader, object>>(setExp, readerParam, targetParam);
            return lambda.Compile();
        }
    }
}
