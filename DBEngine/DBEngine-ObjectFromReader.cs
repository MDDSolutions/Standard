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
        public void ObjectFromReaderWithMetrics<T>(SqlDataReader rdr, ref List<PropertyMapEntry> map, ref PropertyInfo key, ref T r, ref Tracker<T> tracker, bool strict = true, QueryExecutionMetrics metrics = null) where T : class, new()
        {
            metrics ??= QueryExecutionMetrics.Disabled;

            if (map == null)
            {
                using (metrics.MeasureMapBuildTime())
                {
                    PropertyInfo concurrencyproperty = null;
                    if (!strict) concurrencyproperty = EnsureConcurrencyProperty(r, concurrencyproperty);
                    BuildPropertyMap<T>(rdr, ref map, ref key, strict, concurrencyproperty);
                }
            }
            using (metrics.MeasureHydration())
            {
                if (r == null) r = new T();
                ExecutePropertyMap(rdr, map, r);
            }
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
        [Serializable]
        public class DBEnginePostMappingException<T> : Exception
        {
            public T TargetObject { get; }
            public string PropertyName { get; }
            public string PropertyType { get; }
            public string ReaderType { get; }
            public string ObjectValues { get; }

            public DBEnginePostMappingException(
                T targetObject,
                string propertyName,
                string propertyType,
                string readerType,
                string objectValues,
                string message,
                Exception innerException)
                : base(message, innerException)
            {
                TargetObject = targetObject;
                PropertyName = propertyName;
                PropertyType = propertyType;
                ReaderType = readerType;
                ObjectValues = objectValues;
            }
            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.AppendLine(base.ToString());
                sb.AppendLine($"Property: {PropertyName}");
                sb.AppendLine($"PropertyType: {PropertyType}");
                sb.AppendLine($"ReaderType: {ReaderType}");
                sb.AppendLine($"ObjectValues: {ObjectValues}");
                return sb.ToString();
            }
        }
        [Serializable]
        public class DBEngineColumnRequiredException : Exception
        {
            public string PropertyName { get; }
            public string PropertyType { get; }
            public string ColumnName { get; }
            public string ObjectType { get; }
            public bool IsOptional { get; }

            public DBEngineColumnRequiredException(
                string propertyName,
                string propertyType,
                string columnName,
                string objectType,
                bool isOptional,
                string message)
                : base(message)
            {
                PropertyName = propertyName;
                PropertyType = propertyType;
                ColumnName = columnName;
                ObjectType = objectType;
                IsOptional = isOptional;
            }

            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.AppendLine(base.ToString());
                sb.AppendLine($"Property: {PropertyName}");
                sb.AppendLine($"PropertyType: {PropertyType}");
                sb.AppendLine($"ColumnName: {ColumnName}");
                sb.AppendLine($"ObjectType: {ObjectType}");
                sb.AppendLine($"IsOptional: {IsOptional}");
                return sb.ToString();
            }
        }
        [Serializable]
        public class DBEngineMappingException : Exception
        {
            public PropertyMapEntry PropertyMapEntry { get; set; }
            public DBEngineMappingException(
                PropertyMapEntry propertyMapEntry,
                string message,
                Exception innerException)
                : base(message, innerException)
            {
                PropertyMapEntry = propertyMapEntry;
            }
            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.AppendLine(base.ToString());
                sb.AppendLine($"Property: {PropertyMapEntry.Property.Name}");
                sb.AppendLine($"PropertyType: {PropertyMapEntry.Property.PropertyType.FullName}");
                sb.AppendLine($"ColumnName: {PropertyMapEntry.ColumnName}");
                sb.AppendLine($"ObjectType: {PropertyMapEntry.Property.DeclaringType.Name}");
                sb.AppendLine($"ReaderType: {PropertyMapEntry.ReaderType.Name}");
                return sb.ToString();
            }
        }

        //private PropertyInfo PrepareTracking_original<T>(SqlDataReader rdr, ref PropertyInfo key, ref T r, ref Tracker<T> tracker, out Tracked<T> existingtracked, out bool skipLoad) where T : class, new()
        //{
        //    existingtracked = null;
        //    skipLoad = false;
        //    PropertyInfo concurrencyproperty = null;

        //    if (tracker != null)
        //    {
        //        if (Tracked<T>.HasConcurrency)
        //            concurrencyproperty = Tracked<T>.ConcurrencyProperty;

        //        if (r != null)
        //        {
        //            var keyvalue = Tracked<T>.GetKeyValue(r);
        //            if (!IsDefaultOrNull(keyvalue) && tracker.TryGet(keyvalue, out existingtracked))
        //            {
        //                switch (existingtracked.State)
        //                {
        //                    case TrackedState.Unchanged:
        //                    case TrackedState.Modified:
        //                        if (existingtracked.TryGetEntity(out var existing) && !ReferenceEquals(existing, r))
        //                            throw new Exception($"DBEngine.ObjectFromReader ERROR: Tracking has been set to {Tracking} but an object of type {typeof(T).Name} with a key value of {keyvalue} is being (re)loaded but is somehow not the same object as what has been passed to OFR");
        //                        existingtracked.Initializing = true;
        //                        if (DebugLevel >= 200) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 55, "OFR cache hit", r.ToString(), typeof(T).Name));
        //                        break;
        //                    case TrackedState.Initializing:
        //                        throw new Exception($"DBEngine.ObjectFromReader ERROR: Tracking has been set to {Tracking} but an object of type {typeof(T).Name} with a key value of {keyvalue} is being (re)loaded and is already in the Initializing state");
        //                    case TrackedState.Invalid:
        //                    default:
        //                        throw new Exception($"DBEngine.ObjectFromReader ERROR: Tracking has been set to {Tracking} but an object of type {typeof(T).Name} with a key value of {keyvalue} is being (re)loaded and is in an invalid state");
        //                }
        //            }
        //            else
        //            {
        //                throw new Exception($"DBEngine.ObjectFromReader ERROR: Tracking has been set to {Tracking} but an object of type {typeof(T).Name} with a key value of {keyvalue} is being (re)loaded and is not in the tracker");
        //            }
        //        }
        //        else
        //        {
        //            var keyvalue = rdr[Tracked<T>.KeyDBName];
        //            var rdrconcurrency = Tracked<T>.HasConcurrency ? rdr[Tracked<T>.ConcurrencyDBName] : null;
        //            if (tracker.TryGet(keyvalue, out existingtracked))
        //            {
        //                var curstate = existingtracked.State;
        //                switch (curstate)
        //                {
        //                    case TrackedState.Modified:
        //                    case TrackedState.Unchanged:
        //                        if (existingtracked.TryGetEntity(out var existing))
        //                        {
        //                            r = existing;
        //                            var entityconcurrency = Tracked<T>.GetConcurrencyValue?.Invoke(r);
        //                            if (Tracked<T>.HasConcurrency && ValueEquals(rdrconcurrency, entityconcurrency))
        //                            {
        //                                if (DebugLevel >= 200) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 56, "OFR cache hit - concurrency match", r.ToString(), typeof(T).Name));
        //                                TrackerHitCount++;
        //                                skipLoad = true;
        //                                return concurrencyproperty;
        //                            }
        //                            else if (curstate == TrackedState.Unchanged)
        //                            {
        //                                existingtracked.Initializing = true;
        //                                if (DebugLevel >= 200) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 53, "OFR cache update", r.ToString(), typeof(T).Name));
        //                            }
        //                            else
        //                            {
        //                                throw new Exception($"DBEngine.ObjectFromReader ERROR: Tracking has been set to {Tracking} but an object of type {typeof(T).Name} with a key value of {keyvalue} is being loaded and is dirty but the concurrency value in the database does not match the concurrency value of the object - you must either set Tracking to None or ensure that all objects being loaded are not already being tracked as dirty");
        //                            }
        //                        }
        //                        break;
        //                    case TrackedState.Initializing:
        //                        throw new Exception($"DBEngine.ObjectFromReader ERROR: Tracking has been set to {Tracking} but an object of type {typeof(T).Name} with a key value of {keyvalue} is being loaded and is already in the Initializing state");
        //                    case TrackedState.Invalid:
        //                    default:
        //                        existingtracked.BeginInitialization(keyvalue, rdrconcurrency, out r);
        //                        if (DebugLevel >= 200) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 51, "OFR revive", $"Type: {typeof(T).Name} ID: {keyvalue}", typeof(T).Name));
        //                        break;
        //                }
        //            }
        //            else
        //            {
        //                existingtracked = tracker.GetOrAdd(keyvalue, rdrconcurrency, out r);
        //                if (DebugLevel >= 220) Log.Entry(new ObjectTrackerLogEntry("ObjectTracker", 51, "OFR create", $"Type: {typeof(T).Name} ID: {keyvalue}", typeof(T).Name));
        //            }
        //        }
        //    }
        //    else
        //    {
        //        if (r == null) r = new T();
        //        if (Tracked<T>.HasConcurrency)
        //            concurrencyproperty = Tracked<T>.ConcurrencyProperty;
        //    }

        //    return concurrencyproperty;
        //}
        //private void FinalizeTracking<T>(Tracked<T> tracked, T target) where T : class, new()
        //{
        //    if (!tracked.Initializing)
        //        throw new Exception($"DBEngine.ObjectFromReader ERROR: Tracking has been set to {Tracking} but an object of type {typeof(T).Name} with a key value of {Tracked<T>.GetKeyValue(target)} has completed loading but was not in the Initializing state");

        //    tracked.EndInitialization();
        //}

        //private List<PropertyMapEntry> GetPropertyDescriptors<T>(T target, ref PropertyInfo key, bool strict, PropertyInfo concurrencyproperty) where T : class
        //{
        //    var descriptors = new List<PropertyMapEntry>();

        //    foreach (var item in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        //    {
        //        bool include = true;
        //        bool optional = false;
        //        string dbName = item.Name;

        //        foreach (var attr in item.GetCustomAttributes(true))
        //        {
        //            if (attr is DBIgnoreAttribute)
        //                include = false;
        //            if (attr is DBNameAttribute dbna)
        //                dbName = dbna.DBName;
        //            if (attr is DBOptionalAttribute)
        //                optional = true;
        //            if (attr is ListKeyAttribute)
        //                key = item;
        //            if (attr is DBLoadedTimeAttribute)
        //            {
        //                include = false;
        //                item.SetValue(target, DateTime.Now);
        //            }
        //        }

        //        if (!include || !item.CanWrite)
        //            continue;

        //        if (!strict)
        //            optional = item != concurrencyproperty;

        //        var descriptor = new PropertyMapEntry
        //        {
        //            Property = item,
        //            ColumnName = dbName,
        //            Optional = optional,
        //            Handler = CreatePropertyHandler(item),
        //            ForcedOrdinal = target is StringObj ? 0 : (int?)null
        //        };

        //        descriptors.Add(descriptor);
        //    }

        //    return descriptors;
        //}
        //private bool BuildPropertyMapIfNeeded<T>(SqlDataReader rdr, ref List<PropertyMapEntry> map, T target) where T : class
        //{

        //    foreach (var descriptor in map)
        //    {
        //        if (descriptor.Handler == null)
        //            continue;

        //        int ordinal;
        //        if (descriptor.ForcedOrdinal.HasValue)
        //        {
        //            ordinal = descriptor.ForcedOrdinal.Value;
        //            if (ordinal >= rdr.FieldCount)
        //            {
        //                if (!descriptor.Optional)
        //                    throw new Exception($"DBEngine internal error: The column '{descriptor.ColumnName ?? descriptor.Property.Name}' was specified as a property (or DBName attribute) in the '{target.GetType().Name}' object but was not found in a query meant to populate objects of that type - you must either decorate this property with a DBIgnore attribute if you want ObjectFromReader to always ignore it, or a DBOptional attribute if you want ObjectFromReader to use it if it is there, but ignore it if it is not");
        //                else
        //                    continue;
        //            }
        //        }
        //        else
        //        {
        //            if (descriptor.Optional && !ColumnExists(rdr, descriptor.ColumnName))
        //                continue;

        //            try
        //            {
        //                ordinal = rdr.GetOrdinal(descriptor.ColumnName);
        //            }
        //            catch (IndexOutOfRangeException)
        //            {
        //                if (descriptor.Optional)
        //                    continue;
        //                throw new Exception($"DBEngine internal error: The column '{descriptor.ColumnName ?? descriptor.Property.Name}' was specified as a property (or DBName attribute) in the '{target.GetType().Name}' object but was not found in a query meant to populate objects of that type - you must either decorate this property with a DBIgnore attribute if you want ObjectFromReader to always ignore it, or a DBOptional attribute if you want ObjectFromReader to use it if it is there, but ignore it if it is not");
        //            }
        //        }

        //        var handler = descriptor.Handler;
        //        var readerFunc = handler.ReaderFunc ?? ((Func<SqlDataReader, int, object>)((reader, ord) => reader.IsDBNull(ord) ? null : reader.GetValue(ord)));
        //        //var mapAction = new Action<SqlDataReader, object>((reader, targetObj) => handler.Assign(reader, ordinal, targetObj));
        //        var mapAction = BuildCompiledMap(descriptor.Property, ordinal);
        //        string readerTypeName;
        //        try
        //        {
        //            readerTypeName = rdr.GetFieldType(ordinal).FullName;
        //        }
        //        catch
        //        {
        //            readerTypeName = "<unknown>";
        //        }

        //        entries.Add(new PropertyMapEntry
        //        {
        //            Ordinal = ordinal,
        //            ReaderFunc = readerFunc,
        //            MapAction = mapAction,
        //            PropertyName = descriptor.Property.Name,
        //            PropertyTypeName = descriptor.Property.PropertyType.FullName,
        //            ReaderTypeName = readerTypeName
        //        });
        //    }

        //    if (entries.Count == 0)
        //    {
        //        map = null;
        //        return false;
        //    }

        //    entries.Sort((x, y) => x.Ordinal.CompareTo(y.Ordinal));
        //    map = entries;
        //    return true;
        //}
        //private void ExecutePropertyMap_old<T>(SqlDataReader rdr, List<PropertyMapEntry> map, T target) where T : class
        //{
        //    foreach (var entry in map)
        //    {
        //        try
        //        {
        //            entry.MapAction(rdr, target);
        //        }
        //        catch (Exception ex)
        //        {
        //            object valueForError;
        //            try
        //            {
        //                valueForError = entry.ReaderFunc(rdr, entry.Ordinal);
        //            }
        //            catch
        //            {
        //                valueForError = "<error reading value>";
        //            }

        //            string objectvalues = string.Join(", ", typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(pi => $"{pi.Name}={(pi.GetValue(target) == null ? "<null>" : pi.GetValue(target).ToString())}"));

        //            throw new Exception($"DBEngine internal error: Post-mapping error occurred trying to set {target.GetType().Name}.{entry.PropertyName} to {valueForError} the property is an {entry.PropertyTypeName} and the mapper clocked the reader as an {entry.ReaderTypeName} (the data types must match exactly) - see inner exception - values in object: {objectvalues}", ex);
        //        }
        //    }
        //}
        //private void ExecuteSequentialNoMap<T>(SqlDataReader rdr, List<PropertyDescriptor> descriptors, T target) where T : class
        //{
        //    if (descriptors == null || descriptors.Count == 0)
        //        return;

        //    var columnDescriptors = new Dictionary<string, PropertyDescriptor>(StringComparer.InvariantCultureIgnoreCase);
        //    var ordinalDescriptors = new Dictionary<int, List<PropertyDescriptor>>();

        //    foreach (var descriptor in descriptors)
        //    {
        //        if (descriptor.Handler == null)
        //            continue;

        //        if (descriptor.ForcedOrdinal.HasValue)
        //        {
        //            if (!ordinalDescriptors.TryGetValue(descriptor.ForcedOrdinal.Value, out var list))
        //            {
        //                list = new List<PropertyDescriptor>();
        //                ordinalDescriptors[descriptor.ForcedOrdinal.Value] = list;
        //            }
        //            list.Add(descriptor);
        //        }
        //        else if (!string.IsNullOrEmpty(descriptor.ColumnName) && !columnDescriptors.ContainsKey(descriptor.ColumnName))
        //        {
        //            columnDescriptors.Add(descriptor.ColumnName, descriptor);
        //        }
        //    }

        //    foreach (var descriptor in descriptors.Where(d => !d.Optional))
        //    {
        //        if (descriptor.ForcedOrdinal.HasValue)
        //        {
        //            if (descriptor.ForcedOrdinal.Value >= rdr.FieldCount)
        //                throw new Exception($"DBEngine internal error: The column '{descriptor.ColumnName ?? descriptor.Property.Name}' was specified as a property (or DBName attribute) in the '{target.GetType().Name}' object but was not found in a query meant to populate objects of that type - you must either decorate this property with a DBIgnore attribute if you want ObjectFromReader to always ignore it, or a DBOptional attribute if you want ObjectFromReader to use it if it is there, but ignore it if it is not");
        //        }
        //        else if (!ColumnExists(rdr, descriptor.ColumnName))
        //        {
        //            throw new Exception($"DBEngine internal error: The column '{descriptor.ColumnName ?? descriptor.Property.Name}' was specified as a property (or DBName attribute) in the '{target.GetType().Name}' object but was not found in a query meant to populate objects of that type - you must either decorate this property with a DBIgnore attribute if you want ObjectFromReader to always ignore it, or a DBOptional attribute if you want ObjectFromReader to use it if it is there, but ignore it if it is not");
        //        }
        //    }

        //    for (int ordinal = 0; ordinal < rdr.FieldCount; ordinal++)
        //    {
        //        if (ordinalDescriptors.TryGetValue(ordinal, out var ordinalList))
        //        {
        //            foreach (var descriptor in ordinalList)
        //            {
        //                descriptor.Handler.Assign(rdr, ordinal, target);
        //            }
        //            continue;
        //        }

        //        var columnName = rdr.GetName(ordinal);
        //        if (columnDescriptors.TryGetValue(columnName, out var descriptorForColumn))
        //        {
        //            descriptorForColumn.Handler.Assign(rdr, ordinal, target);
        //        }
        //    }
        //}
        //private static bool ColumnExists(SqlDataReader rdr, string columnName)
        //{
        //    if (string.IsNullOrEmpty(columnName))
        //        return false;

        //    for (int i = 0; i < rdr.FieldCount; i++)
        //    {
        //        if (rdr.GetName(i).Equals(columnName, StringComparison.InvariantCultureIgnoreCase))
        //            return true;
        //    }

        //    return false;
        //}
        //private PropertyHandler CreatePropertyHandler(PropertyInfo property)
        //{
        //    var setter = BuildCompiledSetter(property);
        //    var propertyType = property.PropertyType;
        //    var underlyingNullable = Nullable.GetUnderlyingType(propertyType);

        //    if (propertyType == typeof(char) || underlyingNullable == typeof(char))
        //    {
        //        bool isNullable = underlyingNullable == typeof(char);
        //        return new PropertyHandler
        //        {
        //            ReaderFunc = (reader, ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal),
        //            Assign = (reader, ordinal, target) =>
        //            {
        //                if (reader.IsDBNull(ordinal))
        //                {
        //                    if (isNullable)
        //                        setter(target, null);
        //                    else
        //                        setter(target, default(char));
        //                    return;
        //                }

        //                var value = reader.GetValue(ordinal);
        //                char charValue;
        //                if (value is string s && !string.IsNullOrEmpty(s))
        //                    charValue = s[0];
        //                else if (value is char c)
        //                    charValue = c;
        //                else
        //                    charValue = Convert.ToChar(value);

        //                if (isNullable)
        //                    setter(target, (char?)charValue);
        //                else
        //                    setter(target, charValue);
        //            }
        //        };
        //    }

        //    var enumType = propertyType.IsEnum ? propertyType : (underlyingNullable != null && underlyingNullable.IsEnum ? underlyingNullable : null);
        //    if (enumType != null)
        //    {
        //        bool isNullable = underlyingNullable != null && underlyingNullable.IsEnum;
        //        return new PropertyHandler
        //        {
        //            ReaderFunc = (reader, ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal),
        //            Assign = (reader, ordinal, target) =>
        //            {
        //                if (reader.IsDBNull(ordinal))
        //                {
        //                    if (isNullable)
        //                        setter(target, null);
        //                    else
        //                        setter(target, Activator.CreateInstance(enumType));
        //                    return;
        //                }

        //                var raw = reader.GetValue(ordinal);
        //                object enumValue;
        //                if (raw is int || raw is short || raw is long || raw is byte)
        //                    enumValue = Enum.ToObject(enumType, raw);
        //                else
        //                    enumValue = Enum.Parse(enumType, raw.ToString(), EnumParseIgnoreCase);

        //                setter(target, enumValue);
        //            }
        //        };
        //    }

        //    if (typeof(ISerializable).IsAssignableFrom(propertyType) && !propertyType.FullName.Contains("System"))
        //    {
        //        return new PropertyHandler
        //        {
        //            ReaderFunc = (reader, ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal),
        //            Assign = (reader, ordinal, target) =>
        //            {
        //                if (reader.IsDBNull(ordinal))
        //                {
        //                    setter(target, GetDefaultValue(propertyType));
        //                    return;
        //                }

        //                var buffer = reader.GetValue(ordinal) as byte[];
        //                if (buffer != null)
        //                {
        //                    var formatter = new BinaryFormatter();
        //                    using (var stream = new MemoryStream(buffer))
        //                    {
        //                        var obj = formatter.Deserialize(stream);
        //                        setter(target, obj);
        //                    }
        //                }
        //                else
        //                {
        //                    setter(target, GetDefaultValue(propertyType));
        //                }
        //            }
        //        };
        //    }

        //    var readerFunc = GetReaderFunc(propertyType) ?? ((Func<SqlDataReader, int, object>)((reader, ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal)));

        //    return new PropertyHandler
        //    {
        //        ReaderFunc = readerFunc,
        //        Assign = (reader, ordinal, target) =>
        //        {
        //            var value = readerFunc(reader, ordinal);
        //            setter(target, value);
        //        }
        //    };
        //}
        //private static object GetDefaultValue(Type type)
        //{
        //    if (type.IsValueType)
        //        return Activator.CreateInstance(type);
        //    return null;
        //}
        //private class PropertyDescriptor
        //{
        //    public PropertyInfo Property { get; set; }
        //    public string ColumnName { get; set; }
        //    public bool Optional { get; set; }
        //    public PropertyHandler Handler { get; set; }
        //    public int? ForcedOrdinal { get; set; }
        //}
        //public class PropertyHandler
        //{
        //    public Action<SqlDataReader, int, object> Assign { get; set; }
        //    public Func<SqlDataReader, int, object> ReaderFunc { get; set; }
        //}

        //// Helper to build compiled setter
        //private static Action<object, object> BuildCompiledSetter(PropertyInfo property)
        //{
        //    var targetType = property.DeclaringType;
        //    var valueType = property.PropertyType;

        //    var targetExp = Expression.Parameter(typeof(object), "target");
        //    var valueExp = Expression.Parameter(typeof(object), "value");

        //    var castTargetExp = Expression.Convert(targetExp, targetType);
        //    var castValueExp = Expression.Convert(valueExp, valueType);

        //    var propertyExp = Expression.Property(castTargetExp, property);
        //    var assignExp = Expression.Assign(propertyExp, castValueExp);

        //    var lambda = Expression.Lambda<Action<object, object>>(assignExp, targetExp, valueExp);
        //    return lambda.Compile();
        //}
        //// Helper to get type-specific reader
        //private static Func<SqlDataReader, int, object> GetReaderFunc(Type type)
        //{
        //    // Nullable types
        //    if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        //    {
        //        var underlyingType = Nullable.GetUnderlyingType(type);
        //        if (underlyingType == typeof(int))
        //            return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (int?)null : rdr.GetInt32(ordinal);
        //        if (underlyingType == typeof(long))
        //            return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (long?)null : rdr.GetInt64(ordinal);
        //        if (underlyingType == typeof(short))
        //            return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (short?)null : rdr.GetInt16(ordinal);
        //        if (underlyingType == typeof(byte))
        //            return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (byte?)null : rdr.GetByte(ordinal);
        //        if (underlyingType == typeof(bool))
        //            return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (bool?)null : rdr.GetBoolean(ordinal);
        //        if (underlyingType == typeof(float))
        //            return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (float?)null : rdr.GetFloat(ordinal);
        //        if (underlyingType == typeof(double))
        //            return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (double?)null : rdr.GetDouble(ordinal);
        //        if (underlyingType == typeof(decimal))
        //            return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (decimal?)null : rdr.GetDecimal(ordinal);
        //        if (underlyingType == typeof(Guid))
        //            return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (Guid?)null : rdr.GetGuid(ordinal);
        //        if (underlyingType == typeof(DateTime))
        //            return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (DateTime?)null : rdr.GetDateTime(ordinal);
        //        if (underlyingType == typeof(DateTimeOffset))
        //            return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? (DateTimeOffset?)null : rdr.GetFieldValue<DateTimeOffset>(ordinal);
        //        // Add more nullable types as needed
        //    }

        //    // Non-nullable types
        //    if (type == typeof(int))
        //        return (rdr, ordinal) => rdr.GetInt32(ordinal);
        //    if (type == typeof(long))
        //        return (rdr, ordinal) => rdr.GetInt64(ordinal);
        //    if (type == typeof(short))
        //        return (rdr, ordinal) => rdr.GetInt16(ordinal);
        //    if (type == typeof(byte))
        //        return (rdr, ordinal) => rdr.GetByte(ordinal);
        //    if (type == typeof(bool))
        //        return (rdr, ordinal) => rdr.GetBoolean(ordinal);
        //    if (type == typeof(float))
        //        return (rdr, ordinal) => rdr.GetFloat(ordinal);
        //    if (type == typeof(double))
        //        return (rdr, ordinal) => rdr.GetDouble(ordinal);
        //    if (type == typeof(decimal))
        //        return (rdr, ordinal) => rdr.GetDecimal(ordinal);
        //    if (type == typeof(Guid))
        //        return (rdr, ordinal) => rdr.GetGuid(ordinal);
        //    if (type == typeof(DateTime))
        //        return (rdr, ordinal) => rdr.GetDateTime(ordinal);
        //    if (type == typeof(DateTimeOffset))
        //        return (rdr, ordinal) => rdr.GetFieldValue<DateTimeOffset>(ordinal);

        //    // Reference types
        //    if (type == typeof(string))
        //        return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? null : rdr.GetString(ordinal);
        //    if (type == typeof(byte[]))
        //        return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? null : (byte[])rdr.GetValue(ordinal);

        //    // Fallback for other types
        //    return (rdr, ordinal) => rdr.IsDBNull(ordinal) ? null : rdr.GetValue(ordinal);
        //}


    }
}
