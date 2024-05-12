using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using System.Data.SqlClient;
using System.Collections;
using System.Threading.Tasks;

namespace MDDDataAccess
{
    public static class Extensions
    {
        public static PropertyInfo GetDetailListProperty(this Object o, Type t)
        {
            foreach (var item in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (item.CanWrite
                    && !item.PropertyType.IsValueType
                    && (item.PropertyType == t
                        || (item.PropertyType.GenericTypeArguments.Length >= 1
                            && t.GenericTypeArguments.Length >= 1
                            && item.PropertyType.GenericTypeArguments[0] == t.GenericTypeArguments[0]
                            )
                        )
                    )
                {
                    return item;
                }
            }
            return null;
        }
        public static List<SqlParameter> SQLParameterList(this Object o, params string[] exclude)
        {
            var r = new List<SqlParameter>();
            foreach (var item in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var include = true;
                string DBName = null;
                foreach (var attr in item.GetCustomAttributes(true))
                {
                    if (attr is DBIgnoreAttribute)
                        include = false;
                    if (attr is DBNameAttribute)
                        DBName = (attr as DBNameAttribute).DBName;
                    //if (attr is DBOptionalAttribute)
                    //    optional = true;
                    //if (attr is ListKeyAttribute)
                    //    key = item;
                }
                if (include && item.CanWrite && (item.PropertyType.IsValueType || item.PropertyType.IsEnum || item.PropertyType.Equals(typeof(System.String))) && !((IList)exclude).Contains(item.Name) && !((IList)exclude).Contains(DBName))
                {
                    object v = item.GetValue(o, null);
                    if (v == null) v = DBNull.Value;
                    r.Add(new SqlParameter(string.Format("@{0}", item.Name), v));
                }
            }
            return r;
        }
        public static SqlParameter ToSqlParameter(this object o, string name)
        {
            //if (name == null)
            //{ 
            //    var type = o.GetType();
            //    name = type.Name;
            //    var nameattr = type.GetCustomAttribute<DBNameAttribute>();
            //    if (nameattr != null) name = nameattr.DBName;
            //}
            return new SqlParameter(name, o);
        }
        
        public static ParameterStub ToParameterStub(this object o, string name)
        {
            //if (name == null)
            //{
            //    var type = o.GetType();
            //    name = type.Name;
            //    var nameattr = type.GetCustomAttribute<DBNameAttribute>();
            //    if (nameattr != null) name = nameattr.DBName;
            //}
            return new ParameterStub(name, o);
        }
        public static void SyncToNotWorking(this Object o, Object source)
        {
            foreach (var item in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (item.CanWrite && (item.PropertyType.IsValueType || item.PropertyType.IsEnum || item.PropertyType.Equals(typeof(System.String))))
                {
                    item.SetValue(o, item.GetValue(source, null), null);
                }
                else if (item is IEnumerable)
                {
                    foreach (var iitem in (item as IEnumerable))
                    {
                        //have to find items in both lists and do a recursive SyncTo
                        //Problem is if the items are out of order... there has to be some kind of ID matching...
                    }
                }
            }
        }
        /// <summary>
        /// This will look for a SQL procedure with the name of the object type and "_Delete" and then run it with autoparameterization so the procedure must exist and 
        /// any parameters it has must match property names of the object - you can optionally provide a DBEngine instance but it will use Default if you don't
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="obj"></param>
        /// <param name="engine"></param>
        public static void DBDelete<T>(this T obj, DBEngine engine = null)
        {
            if (engine == null) engine = DBEngine.Default;
            engine.DBDelete(obj);
            //string procname = $"{obj.GetType().Name}_Delete";
            //engine.SqlRunProcedure(procname, obj, -1, null);
        }
        /// <summary>
        /// This will look for a SQL procedure with the name of the object and "_Upsert" and then run it (using RunSqlUpdate so the object will update) with autoparameterization so the procedure must exist and
        /// any parameters it has must match property names of the object - you can optionally provide a DBEngine instance but it will use Default if you don't
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="obj"></param>
        /// <param name="engine"></param>
        public static void DBUpsert<T>(this T obj, DBEngine engine = null) where T : new()
        {
            if (engine == null) engine = DBEngine.Default;
            engine.DBUpsert(obj);
            //string procname = $"{obj.GetType().Name}_Upsert";
            //engine.RunSqlUpdate(obj, procname, -1, null);
        }
        public static IList<T> ListBy<T>(this object o, DBEngine engine = null) where T : new()
        {
            if (engine == null) engine = DBEngine.Default;
            return engine.ListBy<T>(o);
        }
        public async static Task<IList<T>> ListByAsync<T>(this object o, DBEngine engine = null) where T : new()
        {
            if (engine == null) engine = DBEngine.Default;
            return await engine.ListByAsync<T>(o).ConfigureAwait(false);
        }
        /// <summary>
        /// This is an extension method alternative for AutoParam which is normally a DBEngine instance method - it allows you to specify an instance but will use Default if you don't - it just might be a
        /// handier way to get to AutoParam
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="obj"></param>
        /// <param name="procname"></param>
        /// <param name="engine"></param>
        /// <returns></returns>
        public static SqlParameter[] AutoParam<T>(this T obj, string procname, DBEngine engine = null)
        {
            if (engine == null) engine = DBEngine.Default;
            return engine.AutoParam(obj, procname);
        }
        public static string Details(this SqlCommand cmd)
        {
            var sb = new StringBuilder();
            sb.Append(cmd.CommandText);
            if (cmd.Parameters != null && cmd.Parameters.Count > 0)
            {
                sb.Append(" - ");
                foreach (SqlParameter item in cmd.Parameters)
                {
                    sb.Append($"{item.ParameterName}:{item.Value}, ");
                }
            }
            return sb.ToString().TrimEnd().TrimEnd(',');
        }
    }
}
