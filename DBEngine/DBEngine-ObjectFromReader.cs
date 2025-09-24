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
            Tracked<T> tracked = null;
            PropertyInfo concurrencyproperty = null;
            // callers must provide a tracker if they want tracking - it is possible to run a query on a trackable object without tracking it
            // if you plan to just discard the results or something
            if (tracker != null) 
            {
                // if tracking is enabled, we need to see if the object already exists in the tracker

                // don't need this for tracking, but other aspects of the method sometimes do stuff with concurrency, so help it out
                if (Tracked<T>.HasConcurrency)
                    concurrencyproperty = Tracked<T>.ConcurrencyProperty;

                // if we already have a reference to the object being loaded, it should be in the tracker
                // if r is null, then at least the caller doesn't know if the object is already being tracked
                if (r != null)
                {
                    var keyvalue = Tracked<T>.GetKeyValue(r);
                    if (!IsDefaultOrNull(keyvalue) && tracker.TryGet(keyvalue, out tracked))
                    {
                        //if we are explicity providing a dirty object then chances are this is an update operation so we let dirty objects through
                        //on the other hand, there should be some kind of indicator that the object is in the middle of a save operation so we don't 
                        //have to assume here - perhaps a BeginSave / EndSave in IObjectTracker?
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
                    //r was not provided so we need to get the key value from the reader and see if the object exists in the tracker
                    var keyvalue = rdr[Tracked<T>.KeyDBName];
                    var rdrconcurrency = Tracked<T>.HasConcurrency ? rdr[Tracked<T>.ConcurrencyDBName] : null;
                    if (tracker.TryGet(keyvalue, out tracked))
                    {
                        var curstate = tracked.State;
                        switch (curstate)
                        {
                            case TrackedState.Modified:
                            case TrackedState.Unchanged:
                                // we can just return the existing object if the concurrency matches whether it is dirty or not
                                // if the concurrency doesn't match, we can reload it if it is unchanged, but if it is dirty then we have a problem
                                if (tracked.TryGetEntity(out var existing))
                                {
                                    r = existing;
                                    var entityconcurrency = Tracked<T>.GetConcurrencyValue?.Invoke(r);
                                    if (Tracked<T>.HasConcurrency && ValueEquals(rdrconcurrency, entityconcurrency))
                                    {
                                        if (DebugLevel >= 200) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 56, "OFR cache hit - concurrency match", r.ToString(), typeof(T).Name));
                                        TrackerHitCount++;
                                        return; // nothing to do - the object is already loaded
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
                                // there was an entry but it has been GC'd - no problem, just revive it - this should be a pretty common occurrence
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
                // not tracking, so just make sure we have an object to load into
                if (r == null) r = new T();
            }

            if (!strict)
            {
                //non-strict mode means we don't match all properties to reader columns but it is still pretty strict... the object must have a populated Key property and a concurrency property
                //this method assumes that the database operation is checking the concurrency property but cannot ensure that
                var keyinfo = AttributeInfo(r, typeof(ListKeyAttribute));
                if (keyinfo == null)
                   keyinfo = AttributeInfo(r, typeof(ListKeyAttribute));
                if (keyinfo.Item1 == null || !keyinfo.Item2)
                    throw new Exception($"DBEngine error: Non-strict ObjectFromReader calls require that the object being loaded have a property marked with ListKeyAttribute and that the property have a value");

                if (concurrencyproperty == null) concurrencyproperty = AttributeProperty<T>(typeof(ListConcurrencyAttribute));
                if (concurrencyproperty == null)
                    throw new Exception($"DBEngine error: Non-strict ObjectFromReader calls require that the object being loaded have a property marked with ListConcurrencyAttribute");

                //at this point all we need to do is not throw an error if properties are missing from the reader - we still map everything we can find - but we do need to make sure
                //that the concurrency property *is* in the reader
            }


            bool nomap = false;

            if (map == null)
            {
                map = new List<PropertyMapEntry>();

                // EnsureCorrectPropertyUsage doesn't work - maybe fix it another day
                //if (Tracking != ObjectTracking.None)
                //{
                //    if (r is INotifyPropertyChanged npc)
                //    {
                //        npc.EnsureCorrectPropertyUsage();
                //    }
                //}






                foreach (var item in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    bool include = true;
                    string DBName = null;
                    bool optional = false;
                    foreach (var attr in item.GetCustomAttributes(true))
                    {
                        if (attr is DBIgnoreAttribute)
                            include = false;
                        if (attr is DBNameAttribute dbna)
                            DBName = dbna.DBName;
                        if (attr is DBOptionalAttribute)
                            optional = true;
                        if (attr is ListKeyAttribute)
                            key = item;
                        if (attr is DBLoadedTimeAttribute)
                        {
                            include = false;
                            nomap = true;
                            map = null;
                            item.SetValue(r, DateTime.Now);
                        }

                    }

                    // in non-strict mode, all properties are optional except the concurrency property
                    // the key property isn't optional either but we've already checked for it above
                    // even if the concurrency property is marked with DBOptional, we still require it to be in the reader in non-strict mode
                    if (!strict) optional = item != concurrencyproperty;

                    if (include && item.CanWrite && (item.PropertyType.Name == "Char" || item.ToString().StartsWith("System.Nullable`1[System.Char]")))
                    {
                        nomap = true;
                        map = null;

                        object o = null;
                        if (DBName != null)
                            o = Convert.IsDBNull(rdr[DBName]) ? null : rdr[DBName];
                        else
                            o = Convert.IsDBNull(rdr[item.Name]) ? null : rdr[item.Name];
                        if (o != null)
                            item.SetValue(r, ((string)o)[0]);
                        else
                            item.SetValue(r, default);

                    }
                    else if (include && item.CanWrite && item.PropertyType.BaseType?.Name != "Enum" && (item.PropertyType.IsValueType || item.PropertyType.Name == "String" || item.PropertyType.Name == "Byte[]"))
                    {
                        try
                        {
                            if (DBName == null) DBName = item.Name;
                            bool optcolfound = false;
                            if (optional)
                            {
                                for (int i = 0; i < rdr.FieldCount; i++)
                                {
                                    if (rdr.GetName(i).Equals(DBName, StringComparison.InvariantCultureIgnoreCase))
                                        optcolfound = true;
                                }
                            }

                            if (!optional || optcolfound)
                            {
                                object o = null;

                                if (r is StringObj)
                                {
                                    o = Convert.IsDBNull(rdr[0]) ? null : rdr[0];
                                    //if (!nomap) map.Add(new Tuple<Action<object, object>, int>(BuildSetAccessor(item.GetSetMethod(true)), 0));
                                    if (!nomap) map.Add(new PropertyMapEntry { 
                                        Ordinal = 0,
                                        //ReaderFunc = GetReaderFunc(item.PropertyType),
                                        //Setter = BuildCompiledSetter(item),
                                        MapAction = BuildCompiledMap(item, 0),
                                        PropertyName = item.Name,
                                        PropertyTypeName = item.PropertyType.FullName,
                                        ReaderTypeName = rdr.GetFieldType(0).FullName
                                    });
                                }
                                else if (DBName != null)
                                {
                                    o = Convert.IsDBNull(rdr[DBName]) ? null : rdr[DBName];
                                    var ordinal = rdr.GetOrdinal(DBName);
                                    //if (!nomap) map.Add(new Tuple<Action<object, object>, int>(BuildSetAccessor(item.GetSetMethod(true)), ordinal));
                                    if (!nomap) map.Add(new PropertyMapEntry { 
                                        Ordinal = ordinal,
                                        //ReaderFunc = GetReaderFunc(item.PropertyType),
                                        //Setter = BuildCompiledSetter(item),
                                        MapAction = BuildCompiledMap(item, ordinal),
                                        PropertyName = item.Name,
                                        PropertyTypeName = item.PropertyType.FullName,
                                        ReaderTypeName = rdr.GetFieldType(ordinal).FullName
                                    });
                                }
                                if (o != null)
                                    item.SetValue(r, o);
                                else
                                    item.SetValue(r, default);
                            }
                        }
                        catch (IndexOutOfRangeException)
                        {
                            if (!optional)
                                throw new Exception($"DBEngine internal error: The column '{DBName ?? item.Name}' was specified as a property (or DBName attribute) in the '{r.GetType().Name}' object but was not found in a query meant to populate objects of that type - you must either decorate this property with a DBIgnore attribute if you want ObjectFromReader to always ignore it, or a DBOptional attribute if you want ObjectFromReader to use it if it is there, but ignore it if it is not");
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"DBEngine internal error: Error occurred mapping property '{DBName ?? item.Name}' in the '{r.GetType().Name}' object - see inner exception", ex);
                        }
                    }
                    else if (include && item.CanWrite && item.PropertyType.BaseType?.Name == "Enum")
                    {
                        nomap = true;
                        map = null;

                        object o = null;
                        if (DBName != null)
                            o = Convert.IsDBNull(rdr[DBName]) ? null : rdr[DBName];
                        else if (r is StringObj)
                            o = Convert.IsDBNull(rdr[0]) ? null : rdr[0];
                        else
                            o = Convert.IsDBNull(rdr[item.Name]) ? null : rdr[item.Name];
                        if (o != null)
                        {
                            if (o is int)
                                item.SetValue(r, Enum.ToObject(item.PropertyType, o));
                            else
                                item.SetValue(r, Enum.Parse(item.PropertyType, o.ToString(), EnumParseIgnoreCase));
                        }
                        else
                            item.SetValue(r, default);
                    }
                    else if (include && item.CanWrite && item.PropertyType is ISerializable && !item.PropertyType.FullName.Contains("System"))
                    {   //this is meant to handle properties that are serializable user types - i.e. classes that implement ISerializable (or are just decorated with [Serializable])
                        //it may be possible to implement an Action<object, object> method that does this but what I'm doing here is disabling the "map" and forcing execution to go
                        //through the non-mapped code here for every row - I don't think that was much worse for performance anyway, but if you want to automatically handle complex
                        //types then something's got to give...

                        // This is mostly untested at this point...

                        nomap = true;
                        map = null;

                        try
                        {

                            byte[] o = null;
                            if (DBName != null)
                                o = Convert.IsDBNull(rdr[DBName]) ? null : rdr[DBName] as byte[];
                            else if (r is StringObj)
                                o = Convert.IsDBNull(rdr[0]) ? null : rdr[0] as byte[];
                            else
                                o = Convert.IsDBNull(rdr[item.Name]) ? null : rdr[item.Name] as byte[];
                            if (o != null)
                            {
                                var formatter = new BinaryFormatter();
                                using (var stream = new MemoryStream(o))
                                {
                                    //if the byte array is a valid serialization of the type of the property, this will work
                                    //...if not, this will probably puke considerably...
                                    var o2 = formatter.Deserialize(stream);
                                    item.SetValue(r, o2);
                                }
                            }
                            else
                            {
                                item.SetValue(r, default);
                            }
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"DBEngine internal error: Error occurred mapping *complex* property '{DBName ?? item.Name}' in the '{r.GetType().Name}' object - see inner exception", ex);
                        }
                    }
                }
                if (!nomap && map != null && map.Count != 0) map.Sort((x, y) => x.Ordinal.CompareTo(y.Ordinal));
            }
            else
            {
                int len = map.Count;
                foreach (var entry in map)
                {
                    try
                    {
                        //o = Convert.IsDBNull(rdr[map[i].Item2]) ? null : rdr[map[i].Item2];
                        //map[i].Item1?.Invoke(r, o);
                        //entry.Setter(r, entry.ReaderFunc(rdr, entry.Ordinal));
                        entry.MapAction(rdr, r);
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
                        string valueType = valueForError == null ? "<null>" : valueForError.GetType().Name;
                        var d = r;
                        string objectvalues = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(pi => $"{pi.Name}={(pi.GetValue(d) == null ? "<null>" : pi.GetValue(d).ToString())}").Aggregate((a, b) => a + ", " + b);
                        throw new Exception(
                            $"DBEngine internal error: Post-mapping error occurred trying to set {r.GetType().Name}.{entry.PropertyName} to {valueForError} the property is an {entry.PropertyTypeName} and the mapper clocked the reader as an {entry.ReaderTypeName} (the data types must match exactly) - see inner exception - values in object: {objectvalues}",
                            ex
                        );
                    }
                }
            }

            if (tracked != null)
            {
                if (!tracked.Initializing)
                    throw new Exception($"DBEngine.ObjectFromReader ERROR: Tracking has been set to {Tracking} but an object of type {typeof(T).Name} with a key value of {Tracked<T>.GetKeyValue(r)} has completed loading but was not in the Initializing state");
                tracked.EndInitialization();
            }
            //if (Tracking == ObjectTracking.ChangeNotification)
            //{
            //    if (r is INotifyPropertyChanged notifier)
            //    {
            //        var aggregator = GetOrCreateEventAggregator<T>();
            //        aggregator.QuickSubscribe(notifier);
            //    }
            //    else
            //    {
            //        throw new Exception($"DBEngine error: Tracking has been set to {Tracking} but type '{r.GetType().Name}' does not implement INotifyPropertyChanged");
            //    }
            //}
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
