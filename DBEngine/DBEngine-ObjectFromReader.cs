using MDDFoundation;
using System;
using System.Collections.Concurrent;
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
using System.Xml.Linq;

namespace MDDDataAccess
{
    public partial class DBEngine
    {
        public bool EnumParseIgnoreCase { get; set; } = true;
        public void ObjectFromReader<T>(SqlDataReader rdr, ref List<PropertyMapEntry> map, ref PropertyInfo key, ref T r, ref Tracker<T> tracker, bool strict = true) where T : class, new()
        {
            if (map == null)
            {
                PropertyInfo concurrencyproperty = null;
                if (!strict) concurrencyproperty = EnsureConcurrencyProperty(r, concurrencyproperty);
                BuildPropertyMap<T>(rdr, ref map, ref key, strict, concurrencyproperty);
            }
            if (r == null) r = new T();
            ExecutePropertyMap(rdr, map, r);
            if (tracker != null) tracker.GetOrAdd(ref r);
        }
        public void ObjectFromReaderWithMetrics_TheWayItSortOfShouldBe<T>(SqlDataReader rdr, ref List<PropertyMapEntry> map, ref PropertyInfo key, ref T r, ref Tracker<T> tracker, bool strict = true, QueryExecutionMetrics metrics = null) where T : class, new()
        {
            using (metrics?.MeasureMapBuildTime())
            {             
                if (map == null)
                {
                    PropertyInfo concurrencyproperty = null;
                    if (!strict) concurrencyproperty = EnsureConcurrencyProperty(r, concurrencyproperty);
                    BuildPropertyMap<T>(rdr, ref map, ref key, strict, concurrencyproperty);
                }
            }
            using (metrics?.MeasureReaderRead())
            {
                if (r == null) r = new T();
                ExecutePropertyMap(rdr, map, r);
            }
            using (metrics?.MeasureTrackerProcessingTime())
            {
                if (tracker != null) tracker.GetOrAdd(ref r);
            }
        }


        public void ObjectFromReaderWithMetrics<T>(SqlDataReader rdr, ref List<PropertyMapEntry> map, ref PropertyInfo key, ref T r, ref Tracker<T> tracker, bool strict = true, QueryExecutionMetrics metrics = null) where T : class, new()
        {
            if (map == null)
            {
                using (metrics.MeasureMapBuildTime())
                {
                    PropertyInfo concurrencyproperty = null;
                    if (!strict) concurrencyproperty = EnsureConcurrencyProperty(r, concurrencyproperty);
                    BuildPropertyMap<T>(rdr, ref map, ref key, strict, concurrencyproperty);
                }
            }
            if (r == null) r = new T();
            ExecutePropertyMap(rdr, map, r);
            if (tracker != null)
            {
                using (metrics.MeasureTrackerProcessingTime())
                {
                    tracker.GetOrAdd(ref r);
                }
            }
        }



        private PropertyInfo EnsureConcurrencyProperty<T>(T target, PropertyInfo concurrencyproperty) where T : class
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

            return concurrencyproperty;
        }
        private void BuildPropertyMap<T>(SqlDataReader rdr, ref List<PropertyMapEntry> map, ref PropertyInfo key, bool strict, PropertyInfo concurrencyproperty)
        {
            map = new List<PropertyMapEntry>();

            var ColumOrdinals = GetColumnOrdinals(rdr);


            foreach (var item in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.CanWrite))
            {
                var type = item.PropertyType;
                if (type.IsValueType || type == typeof(string) || type.IsArray)
                {
                    var entry = new PropertyMapEntry();
                    entry.Optional = !item.GetSetMethod(true).IsPublic;
                    entry.ColumnName = item.Name;
                    entry.Property = item;
                    bool include = true;
                    bool special = false;
                    foreach (var attr in item.GetCustomAttributes(true))
                    {
                        if (attr is DBIgnoreAttribute)
                            include = false;
                        if (attr is DBNameAttribute dbna)
                            entry.ColumnName = dbna.DBName;
                        if (attr is DBOptionalAttribute)
                            entry.Optional = true;
                        if (attr is ListKeyAttribute)
                            key = item;
                        if (attr is DBLoadedTimeAttribute)
                        {
                            //include = false;
                            //nomap = true;
                            //map = null;
                            //item.SetValue(r, DateTime.Now);
                            special = true;
                            entry.Ordinal = -100;
                            entry.MapAction = BuildStaticDateTimeFunc(item);
                        }
                    }
                    if (include && !special)
                    {
                        if (!strict) entry.Optional = item != concurrencyproperty;
                        if (ColumOrdinals.TryGetValue(entry.ColumnName, out var ordinals))
                        {
                            entry.Ordinal = ordinals[0];
                            //when building a map for a single object, any given column should only appear once but I don't necessarily
                            //want to throw if it has more than one - I may have a version of BuildPropertyMap that handles 2 types - then things get a little
                            //more interesting with columns like created_date / modified_date - for now, I'll just end up using the first one I find
                        }
                        else
                        {
                            if (!entry.Optional)
                                throw new DBEngineColumnRequiredException(
                                    item.Name,
                                    item.PropertyType.FullName,
                                    entry.ColumnName,
                                    typeof(T).Name,
                                    entry.Optional,
                                    $"DBEngine internal error: The column '{entry.ColumnName}' was specified as a property (or DBName attribute) in the '{typeof(T).Name}' object but was not found in a query meant to populate objects of that type. " +
                                    "You must either decorate this property with a DBIgnore attribute if you want ObjectFromReader to always ignore it, or a DBOptional attribute if you want ObjectFromReader to use it if it is there, but ignore it if it is not."
                                );
                            else
                                include = false;
                        }
                        if (include)
                        {
                            entry.ReaderType = rdr.GetFieldType(entry.Ordinal);
                            BuildCompiledMap(entry);
                        }
                    }
                    if (include) map.Add(entry);
                }
                else
                {
                    if (DebugLevel > 100)
                    {
                        if (unhandledtypesreported.TryAdd($"{typeof(T).Name}.{item.Name}", true))
                            Log.Entry("ObjectFromReader", 50, $"Unhandled type in {typeof(T).Name}.{item.Name} type full name: {type.FullName}", "");
                    }
                    //what to do with this type?
                }
            }
            map.Sort((x, y) => x.Ordinal.CompareTo(y.Ordinal));
        }
        private readonly ConcurrentDictionary<string,bool> unhandledtypesreported = new ConcurrentDictionary<string,bool>();
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
                    string objectvalues = null;
                    try
                    {
                        objectvalues = string.Join("\r", typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(pi => $"{pi.Name}={(pi.GetValue(target) == null ? "<null>" : pi.GetValue(target).ToString())}"));
                    }
                    catch (Exception ex2)
                    {
                        objectvalues = $"Error getting object values: {ex2.Message}";
                    }

                    throw new DBEnginePostMappingException<T>(
                        target,
                        entry.Property.Name,
                        entry.Property.PropertyType.FullName,
                        entry.ReaderType.Name,
                        objectvalues,
                        $"DBEngine internal error: Post-mapping error occurred trying to set {target.GetType().Name}.{entry.Property.Name} - {entry.ReaderType.Name} -> {entry.Property.PropertyType.FullName}",
                        ex
                    );
                }
            }
        }
        private static MethodInfo ResolveReaderGetter(Type type)
        {
            var t = Nullable.GetUnderlyingType(type) ?? type;

            // Map of CLR types to SqlDataReader getters
            var map = new Dictionary<Type, string>
                {
                    { typeof(bool),          "GetBoolean" },
                    { typeof(byte),          "GetByte" },
                    { typeof(short),         "GetInt16" },
                    { typeof(int),           "GetInt32" },
                    { typeof(long),          "GetInt64" },
                    { typeof(float),         "GetFloat" },        // maps SQL real
                    { typeof(double),        "GetDouble" },
                    { typeof(decimal),       "GetDecimal" },
                    { typeof(DateTime),      "GetDateTime" },
                    { typeof(DateTimeOffset),"GetDateTimeOffset" },
                    { typeof(TimeSpan),      "GetTimeSpan" },
                    { typeof(Guid),          "GetGuid" },
                    { typeof(string),        "GetString" },
                    { typeof(byte[]),        "GetSqlBinary" },    // but usually use GetValue + cast
                };

            if (map.TryGetValue(t, out var name))
            {
                    return typeof(SqlDataReader).GetMethod(name, new[] { typeof(int) });
            }

            // Fallback: use generic GetFieldValue<T>(int)
            var fallback = typeof(SqlDataReader)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)

                .First(m => m.Name == nameof(SqlDataReader.GetFieldValue) &&
                            m.IsGenericMethodDefinition &&
                            m.GetParameters().Length == 1 &&
                            m.GetParameters()[0].ParameterType == typeof(int));

            return fallback.MakeGenericMethod(t);
        }

        private static void BuildCompiledMap(PropertyMapEntry entry)
        {
            try
            {
                var targetType = entry.Property.DeclaringType;
                var propertyType = entry.Property.PropertyType;

                var readerParam = Expression.Parameter(typeof(SqlDataReader), "rdr");
                var targetParam = Expression.Parameter(typeof(object), "target");
                var castTarget = Expression.Convert(targetParam, targetType);

                var ordinalExp = Expression.Constant(entry.Ordinal);
                var isDbNullMethod = typeof(SqlDataReader).GetMethod(nameof(SqlDataReader.IsDBNull));

                Expression valueExp;


                // Special case: byte[] → GetFieldValue<byte[]>
                if (propertyType == typeof(byte[]))
                {
                    var generic = typeof(SqlDataReader)
                        .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                        .First(m => m.Name == nameof(SqlDataReader.GetFieldValue) &&
                                    m.IsGenericMethodDefinition &&
                                    m.GetParameters().Length == 1 &&
                                    m.GetParameters()[0].ParameterType == typeof(int));
                    var getValue = generic.MakeGenericMethod(typeof(byte[]));
                    var readCall = Expression.Call(readerParam, getValue, ordinalExp);
                    valueExp = Expression.Condition(
                        Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                        Expression.Constant(null, typeof(byte[])),
                        readCall
                    );
                }
                // Special case: Char (nullable or not)
                else if (propertyType == typeof(char) ||
                    (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(Nullable<>) &&
                         Nullable.GetUnderlyingType(propertyType) == typeof(char)))
                {
                    var getter = ResolveReaderGetter(typeof(string));
                    var readCall = Expression.Call(readerParam, getter, ordinalExp);
                    var strVar = Expression.Variable(typeof(string), "str");
                    var charMethod = typeof(string).GetMethod("get_Chars", new[] { typeof(int) });

                    Expression nonNullExp;
                    if (propertyType == typeof(char))
                    {
                        // strict char: throw if length != 1
                        var exCtor = typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) });
                        var throwExpr = Expression.Throw(
                            Expression.New(exCtor, Expression.Constant("String value cannot be mapped to char because its length is not exactly 1.")),
                            typeof(char)
                        );

                        nonNullExp = Expression.Block(
                            new[] { strVar },
                            Expression.Assign(strVar, readCall),
                            Expression.Condition(
                                Expression.Equal(Expression.Property(strVar, "Length"), Expression.Constant(1)),
                                Expression.Call(strVar, charMethod, Expression.Constant(0)),
                                throwExpr
                            )
                        );

                        valueExp = Expression.Condition(
                            Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                            Expression.Constant('\0', typeof(char)),
                            nonNullExp
                        );
                    }
                    else
                    {
                        // nullable char: return null if length == 0
                        var charValue = Expression.Call(strVar, charMethod, Expression.Constant(0));
                        nonNullExp = Expression.Block(
                            new[] { strVar },
                            Expression.Assign(strVar, readCall),
                            Expression.Condition(
                                Expression.GreaterThan(Expression.Property(strVar, "Length"), Expression.Constant(0)),
                                Expression.Convert(charValue, propertyType),
                                Expression.Constant(null, propertyType)
                            )
                        );

                        valueExp = Expression.Condition(
                            Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                            Expression.Constant(null, propertyType),
                            nonNullExp
                        );
                    }
                }
                // Special case: enum from string
                else if (propertyType.IsEnum && entry.ReaderType == typeof(string))
                {
                    var getter = ResolveReaderGetter(typeof(string));
                    var readCall = Expression.Call(readerParam, getter, ordinalExp);

                    var enumParse = typeof(Enum).GetMethod(
                        nameof(Enum.Parse),
                        new[] { typeof(Type), typeof(string), typeof(bool) }
                    );
                    var parseCall = Expression.Call(
                        enumParse,
                        Expression.Constant(propertyType),
                        readCall,
                        Expression.Constant(true) // ignore case
                    );

                    valueExp = Expression.Condition(
                        Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                        Expression.Default(propertyType),
                        Expression.Convert(parseCall, propertyType)
                    );
                }
                else
                {
                    // Everything else: use the native getter + convert if needed
                    var getter = ResolveReaderGetter(entry.ReaderType)
                                 ?? typeof(SqlDataReader).GetMethod(nameof(SqlDataReader.GetValue), new[] { typeof(int) });
                    var readCall = Expression.Call(readerParam, getter, ordinalExp);

                    Expression converted = getter.ReturnType == propertyType
                        ? (Expression)readCall
                        : Expression.Convert(readCall, propertyType);

                    // If nullable<T>, wrap conversion inside Nullable<T>
                    if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
                    {
                        var nullValue = Expression.Constant(null, propertyType);
                        valueExp = Expression.Condition(
                            Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                            nullValue,
                            Expression.Convert(readCall, propertyType)
                        );
                    }
                    else if (propertyType == typeof(string))
                    {
                        valueExp = Expression.Condition(
                            Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                            Expression.Constant(null, typeof(string)),
                            readCall
                        );
                    }
                    else
                    {
                        valueExp = Expression.Condition(
                            Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                            Expression.Default(propertyType),
                            converted
                        );
                    }
                }

                // Assign to property
                var setMethod = entry.Property.GetSetMethod(true);
                var setExp = Expression.Call(castTarget, setMethod, valueExp);

                var lambda = Expression.Lambda<Action<SqlDataReader, object>>(setExp, readerParam, targetParam);
                entry.MapAction = lambda.Compile();
            }
            catch (Exception ex)
            {
                throw new DBEngineMappingException(entry,
                    $"DBEngine internal error: Unable to map {entry.Property.DeclaringType.Name}.{entry.Property.Name} - {entry.ReaderType.FullName} -> {entry.Property.PropertyType.FullName} - see inner exception",
                    ex
                );
            }
        }

        private static void BuildCompiledMap_old(PropertyMapEntry entry)
        {
            var targetType = entry.Property.DeclaringType;
            var propertyType = entry.Property.PropertyType;

            var readerParam = Expression.Parameter(typeof(SqlDataReader), "rdr");
            var targetParam = Expression.Parameter(typeof(object), "target");

            // Cast target to correct type
            var castTarget = Expression.Convert(targetParam, targetType);

            // Build reader logic
            Expression valueExp;
            MethodInfo isDbNullMethod = typeof(SqlDataReader).GetMethod("IsDBNull");
            var ordinalExp = Expression.Constant(entry.Ordinal);

            if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                var underlyingType = Nullable.GetUnderlyingType(propertyType);
                var nullValue = Expression.Constant(null, propertyType);

                if (underlyingType == typeof(char))
                {
                    var getter = ResolveReaderGetter(typeof(string));
                    var readCall = Expression.Call(readerParam, getter, ordinalExp);
                    var strVar = Expression.Variable(typeof(string), "str");
                    var charMethod = typeof(string).GetMethod("get_Chars", new[] { typeof(int) });
                    var charValue = Expression.Call(strVar, charMethod, Expression.Constant(0));

                    valueExp = Expression.Condition(
                        Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                        nullValue,
                        Expression.Block(
                            new[] { strVar },
                            Expression.Assign(strVar, readCall),
                            Expression.Condition(
                                Expression.GreaterThan(Expression.Property(strVar, "Length"), Expression.Constant(0)),
                                Expression.Convert(charValue, propertyType),
                                nullValue)));
                }
                else
                {
                    var getter = ResolveReaderGetter(underlyingType);
                    var readCall = Expression.Call(readerParam, getter, ordinalExp);

                    valueExp = Expression.Condition(
                        Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                        nullValue,
                        Expression.Convert(readCall, propertyType));
                }
            }
            else if (propertyType == typeof(string))
            {
                var getter = ResolveReaderGetter(typeof(string));
                valueExp = Expression.Condition(
                    Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                    Expression.Constant(null, typeof(string)),
                    Expression.Call(readerParam, getter, ordinalExp));
            }
            else if (propertyType == typeof(byte[]))
            {
                valueExp = Expression.Condition(
                    Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                    Expression.Constant(null, typeof(byte[])),
                    Expression.Convert(Expression.Call(readerParam, typeof(SqlDataReader).GetMethod("GetValue"), ordinalExp), typeof(byte[])));
            }
            else if (propertyType == typeof(char))
            {
                var getter = ResolveReaderGetter(typeof(string));
                var readCall = Expression.Call(readerParam, getter, ordinalExp);
                var strVar = Expression.Variable(typeof(string), "str");
                var charMethod = typeof(string).GetMethod("get_Chars", new[] { typeof(int) });
                var defaultChar = Expression.Constant('\0', typeof(char));

                // Exception to throw if length != 1
                var exCtor = typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) });
                var throwExpr = Expression.Throw(
                    Expression.New(exCtor, Expression.Constant("String value cannot be mapped to char because its length is not exactly 1.")),
                    typeof(char) // must match the expected return type
                );

                valueExp = Expression.Condition(
                    Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                    defaultChar,
                    Expression.Block(
                        new[] { strVar },
                        Expression.Assign(strVar, readCall),
                        Expression.Condition(
                            Expression.Equal(Expression.Property(strVar, "Length"), Expression.Constant(1)),
                            Expression.Call(strVar, charMethod, Expression.Constant(0)),
                            throwExpr
                        )
                    )
                );
            }
            else if (propertyType.IsEnum && entry.ReaderType == typeof(string))
            {
                var getter = ResolveReaderGetter(typeof(string));
                var readCall = Expression.Call(readerParam, getter, ordinalExp);
                var enumParse = typeof(Enum).GetMethod(
                        "Parse",
                        new[] { typeof(Type), typeof(string), typeof(bool) }
                    );
                var parseCall = Expression.Call(
                    enumParse,
                    Expression.Constant(propertyType),
                    readCall,
                    Expression.Constant(true) // ignoreCase
                );
                valueExp = Expression.Condition(
                    Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                    Expression.Default(propertyType),
                    Expression.Convert(parseCall, propertyType)
                );
            }
            else if (propertyType.IsValueType)
            {
                try
                {
                    var getter = ResolveReaderGetter(entry.ReaderType)
                                 ?? typeof(SqlDataReader).GetMethod("GetValue", new[] { typeof(int) });
                    var call = Expression.Call(readerParam, getter, ordinalExp);
                    valueExp = getter.ReturnType == propertyType ? (Expression)call : Expression.Convert(call, propertyType);
                }
                catch (InvalidOperationException ex)
                {

                    throw new DBEngineMappingException(entry, 
                        $"DBEngine internal error: Unable to map {targetType.Name}.{entry.Property.Name} - {entry.ReaderType.FullName} -> {entry.Property.PropertyType.FullName} - see inner exception",
                        ex
                    );
                }
            }
            else
            {
                valueExp = Expression.Condition(
                    Expression.Call(readerParam, isDbNullMethod, ordinalExp),
                    Expression.Constant(null),
                    Expression.Call(readerParam, typeof(SqlDataReader).GetMethod("GetValue"), ordinalExp));
            }


            // Set property
            var setMethod = entry.Property.GetSetMethod(true);
            var setExp = Expression.Call(castTarget, setMethod, valueExp);

            var lambda = Expression.Lambda<Action<SqlDataReader, object>>(setExp, readerParam, targetParam);
            entry.MapAction = lambda.Compile();
        }
        private static Dictionary<string, List<int>> GetColumnOrdinals(SqlDataReader rdr)
        {
            var dict = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rdr.FieldCount; i++)
            {
                var name = rdr.GetName(i);
                if (!dict.TryGetValue(name, out var list))
                {
                    list = new List<int>();
                    dict[name] = list;
                }
                list.Add(i);
            }
            return dict;
        }

        private static Action<SqlDataReader, object> BuildCompiledMap_original(PropertyInfo property, int ordinal)
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
        private static Action<SqlDataReader, object> BuildStaticDateTimeFunc(PropertyInfo property)
        {
            var targetType = property.DeclaringType;
            var propertyType = property.PropertyType;

            // Make sure it's assignable from DateTime
            var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            if (underlyingType != typeof(DateTime))
                throw new InvalidOperationException($"Property {property.Name} is not DateTime or Nullable<DateTime>.");

            // Parameters: (SqlDataReader rdr, object target)
            var readerParam = Expression.Parameter(typeof(SqlDataReader), "rdr"); // ignored
            var targetParam = Expression.Parameter(typeof(object), "target");

            // Cast target to its real type
            var castTarget = Expression.Convert(targetParam, targetType);

            // Expression for DateTime.Now
            var nowProp = typeof(DateTime).GetProperty(nameof(DateTime.Now));
            var nowExpr = Expression.Property(null, nowProp);

            // Convert to property type (handles Nullable<DateTime>)
            Expression valueExpr = propertyType == typeof(DateTime)
                ? (Expression)nowExpr
                : Expression.Convert(nowExpr, propertyType);

            // Build property set call
            var setMethod = property.GetSetMethod(true);
            var setExpr = Expression.Call(castTarget, setMethod, valueExpr);

            // Final lambda
            return Expression.Lambda<Action<SqlDataReader, object>>(setExpr, readerParam, targetParam).Compile();
        }

    }
    public class PropertyMapEntry
    {
        public int Ordinal { get; set; } = -100000; //default(int) is a valid value - the first column in the reader - this value must be set to a valid value intentionally if the entry is to be used
        public Action<SqlDataReader, object> MapAction; // alternative to Setter + ReaderFunc
        public Type ReaderType { get; set; }
        public PropertyInfo Property { get; set; }
        public string ColumnName { get; set; }
        public bool Optional { get; set; }
        public override string ToString() => $"rdr({Ordinal}): rdr({ColumnName}) -> {Property.Name} - {Property.PropertyType.Name}";
    }
}
