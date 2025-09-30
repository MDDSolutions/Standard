using Microsoft.SqlServer.Server;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace MDDDataAccess
{
    public partial class DBEngine
    {
        private static DBEngine instance = null;
        public static bool IsDefaultInitialized { get => instance != null; }
        public static DBEngine Default 
        {
            get
            {
                if (instance == null) throw new Exception("DBEngine default instance has not been initialized");
                return instance;
            }
            set
            {
                instance = value;
            }
        }
        private DBEngine()
        {

        }
        public DBEngine(string inConnStr, string inDefaultAppName)
        {
            if (string.IsNullOrWhiteSpace(inConnStr)) throw new Exception("DBEngine Constructor: Connection String cannot be blank");
            SetConnectionString(inConnStr);
            DefaultApplicationName = inDefaultAppName;
        }
        public DBEngine(string server, string database, string user, string password, string appname)
        {
            connectionstring = new SqlConnectionStringBuilder();
            connectionstring.DataSource = server;
            connectionstring.InitialCatalog = database;
            connectionstring.UserID = user;
            connectionstring.Password = password;
            DefaultApplicationName = appname;
        }
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private static string applicationkey = "aYX9HMj~xHlw+0Q";
        public static string EncryptConnectionString(string connectionstring, string userkey)
        {
            return Crypto.Encrypt(connectionstring, applicationkey + userkey);
        }
        public static DBEngine NewSecureInstance(string encryptedconnectionstring, string userkey, string inDefaultAppName)
        {
            var db = new DBEngine();
            db.SecureMode = true;
            db.SetConnectionString(Crypto.Decrypt(encryptedconnectionstring, applicationkey + userkey));
            db.DefaultApplicationName = inDefaultAppName;
            return db;
        }
        public DBEngine(string server, string database, string user, string encryptedpassword, string userkey, string appname)
        {
            var pw = Crypto.Decrypt(encryptedpassword, applicationkey + userkey);
            SecureMode = true;
            connectionstring = new SqlConnectionStringBuilder();
            connectionstring.DataSource = server;
            connectionstring.InitialCatalog = database;
            connectionstring.UserID = user;
            connectionstring.Password = pw;
            DefaultApplicationName = appname;
        }
        public static DBEngine NewTrustedConnection(string server, string database, string inDefaultAppName)
        {
            var db = new DBEngine();
            db.connectionstring = new SqlConnectionStringBuilder();
            db.connectionstring.DataSource = server;
            db.connectionstring.InitialCatalog = database;
            db.connectionstring.IntegratedSecurity = true;
            db.DefaultApplicationName = inDefaultAppName;
            return db;
        }
        static Action<object, object> BuildSetAccessor(MethodInfo method)
        {
            var obj = Expression.Parameter(typeof(object), "o");
            var value = Expression.Parameter(typeof(object));

            Expression<Action<object, object>> expr =
                Expression.Lambda<Action<object, object>>(
                    Expression.Call(
                        Expression.Convert(obj, method.DeclaringType),
                        method,
                        Expression.Convert(value, method.GetParameters()[0].ParameterType)),
                    obj,
                    value);

            return expr.Compile();
        }
        public static T ObjectToEnum<T>(Object o) where T : struct, IConvertible
        {
            var ObjectString = Convert.IsDBNull(o) ? default(String) : Convert.ToString(o);
            if (!Enum.TryParse(ObjectString, true, out T retval))
            {
                retval = default(T);
            }
            return retval;
        }
        private static SqlParameter[] StubsToSqlParameters(ParameterStub[] list)
        {
            if (list == null) return null;
            var l = new SqlParameter[list.Length];
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].DBType != null)
                {
                    var p = new SqlParameter(list[i].Name, list[i].Value);
                    p.SqlDbType = list[i].DBType ?? SqlDbType.Int;
                    l[i] = p;
                }
                else
                {
                    l[i] = new SqlParameter(list[i].Name, list[i].Value);
                }
            }
            return l;
        }
        public static List<SqlDataRecord> ToDataRecords<T>(IList<T> data, params string[] exclude)
        {
            var sqlmeta = new List<SqlMetaData>();
            var properties = new List<PropertyInfo>();
            foreach (var item in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (item.CanWrite && (item.PropertyType.IsValueType || item.PropertyType.IsEnum || item.PropertyType.Equals(typeof(System.String))) && !exclude.Contains(item.Name))
                {
                    SqlDbType sdbtyp = GetSqlType(item.PropertyType);
                    properties.Add(item);
                    if (sdbtyp == SqlDbType.VarChar)
                        sqlmeta.Add(new SqlMetaData(item.Name, sdbtyp, SqlMetaData.Max));
                    else if (sdbtyp == SqlDbType.Decimal)
                        sqlmeta.Add(new SqlMetaData(item.Name, sdbtyp, 38, 9));
                    else
                        sqlmeta.Add(new SqlMetaData(item.Name, sdbtyp));
                }
            }
            var l = new List<SqlDataRecord>();
            foreach (var item in data)
            {
                SqlDataRecord ret = new SqlDataRecord(sqlmeta.ToArray());
                for (int i = 0; i < sqlmeta.Count; i++)
                {
                    ret.SetValue(i, properties[i].GetValue(item, null));
                }
                l.Add(ret);
            }
            return l;
        }
        public static List<SqlDataRecord> ToDataRecordsExplicit<T>(IList<T> data, params string[] include)
        {
            var sqlmeta = new List<SqlMetaData>();
            var properties = new List<PropertyInfo>();

            foreach (var item in include)
            {
                var prop = typeof(T).GetProperty(item);
                properties.Add(prop);
                SqlDbType sdbtyp = GetSqlType(prop.PropertyType);
                if (sdbtyp == SqlDbType.VarChar)
                    sqlmeta.Add(new SqlMetaData(item, sdbtyp, SqlMetaData.Max));
                else if (sdbtyp == SqlDbType.Decimal)
                    sqlmeta.Add(new SqlMetaData(item, sdbtyp, 38, 9));
                else if (sdbtyp == SqlDbType.Binary)
                    sqlmeta.Add(new SqlMetaData(item, SqlDbType.VarBinary, SqlMetaData.Max));
                else
                    sqlmeta.Add(new SqlMetaData(item, sdbtyp));
            }



            //foreach (var item in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            //{
            //    if (item.CanWrite && (item.PropertyType.IsValueType || item.PropertyType.IsEnum || item.PropertyType.Equals(typeof(System.String))) && include.Contains(item.Name))
            //    {
            //        SqlDbType sdbtyp = GetSqlType(item.PropertyType);
            //        properties.Add(item);
            //        if (sdbtyp == SqlDbType.VarChar)
            //            sqlmeta.Add(new SqlMetaData(item.Name, sdbtyp, SqlMetaData.Max));
            //        else if (sdbtyp == SqlDbType.Decimal)
            //            sqlmeta.Add(new SqlMetaData(item.Name, sdbtyp, 38, 9));
            //        else
            //            sqlmeta.Add(new SqlMetaData(item.Name, sdbtyp));
            //    }
            //}
            var l = new List<SqlDataRecord>();
            foreach (var item in data)
            {
                SqlDataRecord ret = new SqlDataRecord(sqlmeta.ToArray());
                for (int i = 0; i < sqlmeta.Count; i++)
                {
                    ret.SetValue(i, properties[i].GetValue(item, null));
                }
                l.Add(ret);
            }
            return l;
        }
        public static DataTable ToDataTable<T>(IList<T> data)
        {
            PropertyDescriptorCollection props =
                TypeDescriptor.GetProperties(typeof(T));
            DataTable table = new DataTable();
            for (int i = 0; i < props.Count; i++)
            {
                System.ComponentModel.PropertyDescriptor prop = props[i];
                table.Columns.Add(prop.Name, prop.PropertyType);
            }
            object[] values = new object[props.Count];
            foreach (T item in data)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = props[i].GetValue(item);
                }
                table.Rows.Add(values);
            }
            return table;
        }
        public static DateTime BuildTime()
        {
            Version version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            DateTime buildtime = new DateTime(2000, 1, 1).AddDays(version.Build).AddSeconds(version.Revision * 2);
            if (buildtime.IsDaylightSavingTime())
                buildtime = buildtime.AddHours(1);
            return buildtime;
        }
        //public static SqlParameter FixParameter(SqlParameter param)
        //{
        //    if (param.Value is IEnumerable && !(param.Value is string) && !(param.Value is byte[]))
        //        param.SqlDbType = SqlDbType.Structured;
        //    if (param.Value is ISerializable && !param.Value.GetType().FullName.Contains("System"))
        //    {   // this is meant to handle properties that are serializable user types - i.e. classes that implement ISerializable (or are just decorated with [Serializable])
        //        // it is mostly untested at this point
        //        var formatter = new BinaryFormatter();
        //        using (var stream = new MemoryStream())
        //        {
        //            formatter.Serialize(stream, param.Value);
        //            param.Value = stream.ToArray();
        //        }
        //    }
        //    if (param.Value == null) param.Value = DBNull.Value;
        //    return param;
        //}
        public static void ParameterizeCommand(SqlParameter[] list, SqlCommand cmd)
        {
            if (list != null)
            {
                foreach (var item in list)
                {
                    bool addcurrent = true;
                    if (item.Value is IEnumerable && !(item.Value is string) && !(item.Value is byte[]))
                    {
                        bool found = false;
                        foreach (var rec in item.Value as IEnumerable)
                        {
                            found = true;
                            break;
                        }
                        if (!found)
                            addcurrent = false;
                        else
                            item.SqlDbType = SqlDbType.Structured;
                    }
                    if (item.Value is ISerializable && !item.Value.GetType().FullName.Contains("System"))
                    {   // this is meant to handle properties that are serializable user types - i.e. classes that implement ISerializable (or are just decorated with [Serializable])
                        // it is mostly untested at this point
                        var formatter = new BinaryFormatter();
                        using (var stream = new MemoryStream())
                        {
                            formatter.Serialize(stream, item.Value);
                            item.Value = stream.ToArray();
                        }
                    }
                    if (item.Value == null) item.Value = DBNull.Value;
                    if (addcurrent) cmd.Parameters.Add(item);
                }
            }
        }
        public static SqlParameter GetParameter(Expression<Func<object>> expression)
        {
            string name;
            if (expression.Body is MemberExpression)
            {
               name = ((MemberExpression)expression.Body).Member.Name;
            }
            else
            {
                var op = ((UnaryExpression)expression.Body).Operand;
                name = ((MemberExpression)op).Member.Name;
            }
            return new SqlParameter(name, expression.Compile().DynamicInvoke());
        }
        public static string PrintExecStatement(SqlCommand cmd, bool suppresserror = false)
        {
            try
            {
                if (cmd.CommandType != CommandType.StoredProcedure)
                {
                    return cmd.CommandText;
                }
                var plist = ProcedureParameterList(cmd);

                var sb = new StringBuilder();
                StringBuilder sbselect = null;

                //foreach (var procparm in plist.Join(cmd.Parameters.OfType<SqlParameter>(), x => x.name, y => y.ParameterName, (x, y) => new { x, y }))
                foreach (var procparm in plist.Where(x => x.is_output))
                {
                    var cmdparm = cmd.Parameters.OfType<SqlParameter>().Where(x => x.ParameterName.TrimStart('@').Equals(procparm.name.TrimStart('@'), StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                    if (cmdparm == null) throw new Exception("Output parameters must be specified");
                    if (sbselect == null)
                    {
                        sb.AppendLine($"DECLARE {procparm.name} {procparm.SQLDataTypeString()} = {PrintSqlValue(cmdparm.Value)},");
                        sbselect = new StringBuilder();
                        sbselect.Append($"SELECT {procparm.name} AS {procparm.name.TrimStart('@')},");
                    }
                    else
                    {
                        sb.AppendLine($"\t{procparm.name} {procparm.SQLDataTypeString()} = {PrintSqlValue(cmdparm.Value)},");
                        sbselect.Append($" {procparm.name} AS {procparm.name.TrimStart('@')},");
                    }
                }
                if (sbselect != null)
                {
                    sb = new StringBuilder(sb.ToString().Trim().TrimEnd(',') + ";\r\n");
                    sbselect = new StringBuilder(sbselect.ToString().TrimEnd(',') + ';');
                }

                sb.AppendLine($"EXEC {cmd.CommandText}");
                var lastcomma = 0;
                bool lastcomment = false;
                if (cmd.Parameters != null && cmd.Parameters.Count > 0)
                {
                    foreach (var procparm in plist)
                    {
                        var cmdparm = cmd.Parameters.OfType<SqlParameter>().Where(x => x.ParameterName.TrimStart('@').Equals(procparm.name.TrimStart('@'), StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                        if (cmdparm == null)
                        {
                            sb.AppendLine($"\t--{procparm.name} = NULL,\t\t--{procparm.SQLDataTypeString()}");
                            lastcomment = true;
                        }
                        else if (procparm.is_output)
                        {
                            sb.Append($"\t{procparm.name} = {cmdparm.ParameterName} OUTPUT,");
                            lastcomma = sb.Length;
                            sb.AppendLine($"\t\t--{procparm.SQLDataTypeString()}");
                            lastcomment = false;
                        }
                        else
                        {
                            sb.Append($"\t{(procparm.name.StartsWith("@") ? procparm.name : $"@{procparm.name}")} = {PrintSqlValue(cmdparm.Value)},");
                            lastcomma = sb.Length;
                            sb.AppendLine($"\t\t--{procparm.SQLDataTypeString()}");
                            lastcomment = false;
                        }
                    }
                }
                var str = sb.ToString();
                if (lastcomment) str = str.Remove(str.LastIndexOf(','), 1);
                if (lastcomma > 0) str = str.Remove(lastcomma - 1, 1);

                sb = new StringBuilder(str);

                str = sb.ToString() + (sbselect == null ? "" : sbselect?.ToString());
                Console.WriteLine(str);
                return str;
            }
            catch (Exception ex)
            {
                if (suppresserror) return $"EXEC {cmd.CommandText} -- could not generate parameter list";
                throw ex;
            }
        }
        public static string PrintSqlValue(object obj)
        {
            if (obj == null) return "NULL";
            if (obj == DBNull.Value) return "NULL";
            if (obj is string || obj is DateTime) return $"'{obj}'";
            return obj.ToString();
        }
        private static readonly ConcurrentDictionary<(Type, Type), PropertyInfo> _propertyCache = new ConcurrentDictionary<(Type, Type), PropertyInfo>();
        public static PropertyInfo AttributeProperty<T>(Type attributetype)
        {
            var key = (typeof(T), attributetype);
            var prop = _propertyCache.GetOrAdd(
                key,
                k => typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(x => Attribute.IsDefined(x, attributetype)));
            return prop;
        }
        /// <summary>
        /// Retrieves information about a property of the specified type that is decorated with a given attribute type
        /// and determines whether the property's value is non-default.
        /// </summary>
        /// <remarks>A property's value is considered non-default if it is not <see langword="null"/> and,
        /// for value types, it does not equal the default value of its type.</remarks>
        /// <typeparam name="T">The type of the object to inspect for the attribute.</typeparam>
        /// <param name="r">The instance of the object to check for the attribute.</param>
        /// <param name="attributetype">The type of the attribute to search for on the object's properties.</param>
        /// <returns>A <see cref="Tuple{T1, T2}"/> where the first item is the <see cref="PropertyInfo"/> of the property
        /// decorated with the specified attribute, or <see langword="null"/> if no such property exists.  The second
        /// item is a <see langword="bool"/> indicating whether the property's value is non-default.  IMPORTANT: this method
        /// will not return null - it will always return a valid tuple - a null in Item1 means the property did not exist.
        /// The existence of the tuple means the information has been retrieved</returns>
        public static Tuple<PropertyInfo, bool> AttributeInfo<T>(T r, Type attributetype)
        {
            var prop = AttributeProperty<T>(attributetype);
            if (prop != null)
            {
                var value = prop.GetValue(r);
                if (value == null) return new Tuple<PropertyInfo, bool>(prop, false);
                if (prop.PropertyType.IsValueType)
                {
                    var defaultValue = Activator.CreateInstance(prop.PropertyType);
                    if (value.Equals(defaultValue)) return new Tuple<PropertyInfo, bool>(prop, false);
                }
                return new Tuple<PropertyInfo, bool>(prop, true);
            }
            return new Tuple<PropertyInfo, bool>(null, false);
        }
        public static bool ValueEquals(object val1, object val2)
        {
            if (val1 == null && val2 == null) return true;
            if (val1 == null || val2 == null) return false;
            if (ReferenceEquals(val1, val2)) return true;

            if (val1 is ValueType && val2 is ValueType)
                return val1.Equals(val2);

            if (val1 is string s1 && val2 is string s2)
                return string.Equals(s1, s2, StringComparison.Ordinal);

            if (val1 is byte[] b1 && val2 is byte[] b2)
                return b1.SequenceEqual(b2);

            // Fast path for other primitive arrays (int[], long[], etc.)
            if (val1 is Array arr1 && val2 is Array arr2)
            {
                var t = arr1.GetType().GetElementType();
                if (t != null && t.IsPrimitive)
                {
                    if (arr1.Length != arr2.Length) return false;
                    for (int i = 0; i < arr1.Length; i++)
                        if (!Equals(arr1.GetValue(i), arr2.GetValue(i)))
                            return false;
                    return true;
                }
                // Fallback for non-primitive arrays
                return arr1.Cast<object>().SequenceEqual(arr2.Cast<object>());
            }

            // Value types and other reference types
            return val1.Equals(val2);
        }
        public static bool IsDefaultOrNull(object key)
        {
            if (key == null)
                return true;

            var type = key.GetType();

            // Int32: treat 0 as default
            if (type == typeof(int))
                return (int)key == 0;           
            
            // String: treat null or empty as default
            if (type == typeof(string))
                return string.IsNullOrEmpty((string)key);

            // Guid: treat Guid.Empty as default
            if (type == typeof(Guid))
                return (Guid)key == Guid.Empty;

            // Long: treat 0 as default
            if (type == typeof(long))
                return (long)key == 0L;

            // Add more types as needed...

            // For other value types, compare to Activator.CreateInstance(type)
            if (type.IsValueType)
                return key.Equals(Activator.CreateInstance(type));

            // For all other cases, just check for null
            return false;
        }
        public static int TrackerHitCount { get; private set;} = 0;
        public static bool IsProcedure(string cmdtext)
        {
            return !string.IsNullOrWhiteSpace(cmdtext) && !WhitespaceOrReserved.IsMatch(cmdtext);
        }
        const string WhitespaceOrReservedPattern = @"[\s;/\-+*]|^vacuum$|^commit$|^rollback$|^revert$";
        static Regex WhitespaceOrReserved = new Regex(WhitespaceOrReservedPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }
}


//public static T ObjectFromReader<T>(SqlDataReader rdr, ref List<Tuple<PropertyInfo, String>> map) where T: class, new()
//{
//    var r = new T();

//    if (map == null)
//    {
//        map = new List<Tuple<PropertyInfo, string>>();
//        foreach (var item in r.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
//        {
//            bool include = true;
//            string DBName = null;
//            foreach (var attr in item.GetCustomAttributes(true))
//            {
//                if (attr is DBIgnoreAttribute)
//                    include = false;
//                if (attr is DBNameAttribute)
//                    DBName = (attr as DBNameAttribute).DBName;
//            }

//            if (include && (item.CanWrite && (item.PropertyType.IsValueType || item.PropertyType.Name == "String")))
//            {
//                object o;
//                if (DBName != null)
//                {
//                    o = Convert.IsDBNull(rdr[DBName]) ? null : rdr[DBName];
//                    map.Add(new Tuple<PropertyInfo, string>(item, DBName));
//                }
//                else if (r is StringObj)
//                {
//                    o = Convert.IsDBNull(rdr[0]) ? null : rdr[0];
//                    map.Add(new Tuple<PropertyInfo, string>(item, rdr.GetName(0)));
//                }
//                else
//                {
//                    o = Convert.IsDBNull(rdr[item.Name]) ? null : rdr[item.Name];
//                    map.Add(new Tuple<PropertyInfo, string>(item, item.Name));
//                }
//                if (o != null)
//                    item.SetValue(r, o);
//            }
//        }
//    }
//    else
//    {
//        int len = map.Count;
//        for (int i = 0; i < len; i++)
//            map[i].Item1.SetValue(r, Convert.IsDBNull(rdr[map[i].Item2]) ? null : rdr[map[i].Item2]);
//    }
//    return r;
//}
//public static T ObjectFromReader<T>(SqlDataReader rdr) where T: class, new()
//{
//    var r = new T();
//        foreach (var item in r.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
//        {
//            if (item.CanWrite && (item.PropertyType.IsValueType || item.PropertyType.Name == "String"))
//            {
//                Object o = null;
//                if (r is StringObj)
//                    o = Convert.IsDBNull(rdr[0]) ? null : rdr[0];
//                else
//                    o = Convert.IsDBNull(rdr[item.Name]) ? null : rdr[item.Name];
//                if (o != null)
//                    item.SetValue(r, o);
//            }
//        }
//    return r;
//}
//public static T ObjectFromReader<T>(SqlDataReader rdr, ref List<Tuple<Action<object, object>, String>> map, ref PropertyInfo key) where T: class, new()
//{
//    var r = new T();

//    if (map == null)
//    {
//        map = new List<Tuple<Action<object, object>, String>>();
//        foreach (var item in r.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
//        {
//            bool include = true;
//            string DBName = null;
//            bool optional = false;
//            foreach (var attr in item.GetCustomAttributes(true))
//            {
//                if (attr is DBIgnoreAttribute)
//                    include = false;
//                if (attr is DBNameAttribute)
//                    DBName = (attr as DBNameAttribute).DBName;
//                if (attr is DBOptionalAttribute)
//                    optional = true;
//                if (attr is ListKeyAttribute)
//                    key = item;
//            }

//            if (include && (item.CanWrite && (item.PropertyType.IsValueType || item.PropertyType.Name == "String" || item.PropertyType.Name == "Byte[]")))
//            {
//                try
//                {
//                    object o = null;
//                    if (DBName != null)
//                    {
//                        o = Convert.IsDBNull(rdr[DBName]) ? null : rdr[DBName];
//                        map.Add(new Tuple<Action<object, object>, String>(BuildSetAccessor(item.GetSetMethod()), DBName));
//                    }
//                    else if (r is StringObj)
//                    {
//                        o = Convert.IsDBNull(rdr[0]) ? null : rdr[0];
//                        map.Add(new Tuple<Action<object, object>, String>(BuildSetAccessor(item.GetSetMethod()), rdr.GetName(0)));
//                    }
//                    else
//                    {
//                        o = Convert.IsDBNull(rdr[item.Name]) ? null : rdr[item.Name];
//                        map.Add(new Tuple<Action<object, object>, String>(BuildSetAccessor(item.GetSetMethod()), item.Name));
//                    }
//                    if (o != null)
//                        item.SetValue(r, o);
//                }
//                catch (IndexOutOfRangeException)
//                {
//                    if (!optional)
//                        throw new Exception($"DBEngine internal error: The column '{DBName ?? item.Name}' was specified as a property (or DBName attribute) in the '{r.GetType().Name}' object but was not found in a query meant to populate objects of that type - you must either decorate this property with a DBIgnore attribute if you want ObjectFromReader to always ignore it, or a DBOptional attribute if you want ObjectFromReader to use it if it is there, but ignore it if it is not");
//                }
//            }
//        }
//    }
//    else
//    {
//        int len = map.Count;
//        for (int i = 0; i < len; i++)
//            map[i].Item1(r, Convert.IsDBNull(rdr[map[i].Item2]) ? null : rdr[map[i].Item2]);
//    }
//    return r;
//}
