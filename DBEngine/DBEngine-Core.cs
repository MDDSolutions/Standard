using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MDDDataAccess
{
    public partial class DBEngine
    {
        public bool AllowAdHoc { get; set; } = false;
        public bool SqlDependencyStarted { get; set; }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private bool securemode = false;
        public bool SecureMode 
        { 
            get => securemode; 
            set
            {
                if (!value) 
                    throw new Exception("Cannot remove SecureMode from an existing Instance");
                else
                    securemode = value;
            }
        }
        private string contextinfo = null;
        public string ContextInfo
        {
            get => contextinfo;
            set
            {
                if (value.Length > 64) throw new ArgumentException("ContextInfo cannot be larger than 64 characters");
                contextinfo = value;
            }
        }

        public int DefaultConnectionTimeout { get; set; } = 30;
        public int CommandTimeout { get; set; } = 30;
        public string DefaultApplicationName { get; set; }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private SqlConnectionStringBuilder connectionstring = null;
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private SqlConnectionStringBuilder secureconnectionstring = null;
        override public string ToString()
        {
            return $"{connectionstring.DataSource} - {connectionstring.InitialCatalog} - " + (connectionstring.IntegratedSecurity ? "<Integrated>" : $"<SQL User: {connectionstring.UserID}>");
        }   
        public SqlConnectionStringBuilder ConnectionString 
        {
            get
            {
                if (SecureMode)
                {
                    if (secureconnectionstring == null)
                    {
                        var scs = new SqlConnectionStringBuilder(connectionstring.ConnectionString);
                        scs.Remove("Password");
                        secureconnectionstring = scs;
                    }
                    return secureconnectionstring;
                }
                return connectionstring;
            }
            set => connectionstring = value; 
        }
        public void UseDatabase(string dbname)
        {
            connectionstring.InitialCatalog = dbname;
        }
        public bool DBConnected { get; set; } = false;
        public bool DBConnectionError { get; set; } = false;
        public Exception DBException { get; set; } = null;
        public TimeSpan LastSQlCommandElapsed { get; set; } = TimeSpan.MaxValue;

        public void SetConnectionString(string inConnStr)
        {
            connectionstring = new SqlConnectionStringBuilder(inConnStr);
        }
        private async Task<SqlConnection> getconnectionasync(CancellationToken CancellationToken, int ConnectionTimeout = -1, string? ApplicationName = null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(connectionstring.ConnectionString))
                {
                    if (ApplicationName == null)
                        connectionstring.ApplicationName = DefaultApplicationName;
                    else
                        connectionstring.ApplicationName = ApplicationName;
                    if (ConnectionTimeout == -1)
                        connectionstring.ConnectTimeout = DefaultConnectionTimeout;
                    else
                        connectionstring.ConnectTimeout = ConnectionTimeout;
                    var cn = new SqlConnection(connectionstring.ConnectionString);
                    await cn.OpenAsync(CancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(ContextInfo))
                    {
                        using (SqlCommand cmd = new SqlCommand("DECLARE @context_bin VARBINARY(128);SET @context_bin = CONVERT(VARBINARY(128), @context_str);SET CONTEXT_INFO @context_bin;", cn))
                        {
                            cmd.Parameters.AddWithValue("@context_str", ContextInfo);
                            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }
                    DBConnected = true;
                    return cn;
                }
                else
                {
                    DBConnected = false;
                    DBConnectionError = true;
                    return null;
                }
            }
            catch (Exception ex)
            {
                DBConnected = false;
                DBConnectionError = true;
                DBException = ex;
                if (ex.Message.StartsWith("A network-related or instance-specific error occurred while establishing a connection to SQL Server"))
                    throw new Exception($"Unable to connect to {connectionstring.DataSource}");
                else
                    throw ex;
            }
        }
        public async Task<SqlConnection> GetConnectionAsync(CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null)
        {
            if (SecureMode) throw new Exception("Cannot return a SqlConnection Object in SecureMode");
            return await getconnectionasync(CancellationToken, ConnectionTimeout, ApplicationName).ConfigureAwait(false);
        }
        private SqlConnection getconnection(int ConnectionTimeout = -1, string ApplicationName = null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(connectionstring.ConnectionString))
                {
                    if (ApplicationName == null)
                        connectionstring.ApplicationName = DefaultApplicationName;
                    else
                        connectionstring.ApplicationName = ApplicationName;
                    if (ConnectionTimeout == -1)
                        connectionstring.ConnectTimeout = DefaultConnectionTimeout;
                    else
                        connectionstring.ConnectTimeout = ConnectionTimeout;
                    var cn = new SqlConnection(connectionstring.ConnectionString);
                    cn.Open();
                    if (!string.IsNullOrWhiteSpace(ContextInfo))
                    {
                        using (SqlCommand cmd = new SqlCommand("DECLARE @context_bin VARBINARY(128);SET @context_bin = CONVERT(VARBINARY(128), @context_str);SET CONTEXT_INFO @context_bin;", cn))
                        {
                            cmd.Parameters.AddWithValue("@context_str", ContextInfo);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    DBConnected = true;
                    return cn;
                }
                else
                {
                    DBConnected = false;
                    DBConnectionError = true;
                    return null;
                }
            }
            catch (Exception ex)
            {
                DBConnected = false;
                DBConnectionError = true;
                DBException = ex;
                if (ex.Message.StartsWith("A network-related or instance-specific error occurred while establishing a connection to SQL Server"))
                    throw new Exception($"Unable to connect to {connectionstring.DataSource}");
                else
                    throw ex;
            }
        }
        public SqlConnection GetConnection(int ConnectionTimeout = -1, string ApplicationName = null)
        {
            if (SecureMode) throw new Exception("Cannot return a SqlConnection Object in SecureMode");
            return getconnection(ConnectionTimeout, ApplicationName);
        }


        public void AutoInsert<T>(IList<T> list, string tablename = null, int ConnectionTimeout = -1, string ApplicationName = null)
        {
            if (tablename == null) tablename = typeof(T).Name;
            if (list != null && list.Count > 0)
            {
                var plist = GetParamList(list[0], tablename);

                var columns = string.Join(",", plist.Where(x => !x.is_identity).Select(x => x.name.TrimStart('@')));
                var values = string.Join(",", plist.Where(x => !x.is_identity).Select(x => x.name));

                var cmdtext = $"INSERT {tablename} ({columns}) VALUES ({values});";

                using (var cn = getconnection(ConnectionTimeout, ApplicationName))
                {
                    if (cn != null)
                    {
                        using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                        {
                            cmd.CommandTimeout = CommandTimeout;


                            ParameterizeCommand(AutoParam(list[0], tablename), cmd);

                            foreach (var item in list)
                            {
                                for (int i = 0; i < cmd.Parameters.Count; i++)
                                {
                                    cmd.Parameters[plist[i].name].Value = plist[i].ObjectProperty.GetValue(item);
                                }
                                ExecuteNonQuery(cmd);
                            }
                        }
                    }
                }
            }
        }
        public IList<ProcedureParameter> ProcedureParameterList(string procname)
        {
            bool allowadhoc = AllowAdHoc;
            AllowAdHoc = true;
            try
            {
                return SqlRunQueryWithResults<ProcedureParameter>(
                    @"  DECLARE @id INT = OBJECT_ID(@procname);
                        IF (OBJECTPROPERTY(@id,'IsProcedure') = 1)
	                        SELECT p.name, is_output, has_default_value, t.name AS type_name, p.max_length, p.precision, p.scale FROM sys.parameters p JOIN sys.types t ON t.user_type_id = p.system_type_id WHERE object_id = @id;
                        ELSE
	                        SELECT '@' + c.name AS name, CAST(c.default_object_id AS BIT) AS has_default_value, c.is_identity, t.name AS type_name, c.max_length, c.precision, c.scale FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.system_type_id WHERE OBJECT_ID = @id;",
                    false, -1, null, new SqlParameter("@procname", procname));
            }
            catch (Exception ex)
            {
                throw ex;
            }
            finally
            {
                AllowAdHoc = allowadhoc;
            }
        }
        public static IList<ProcedureParameter> ProcedureParameterList(SqlCommand incmd)
        {
            if (incmd.CommandType != CommandType.StoredProcedure) throw new ArgumentException("Command must be a stored procedure.");

            var l = new List<ProcedureParameter>();
            using (SqlConnection cn = new SqlConnection(incmd.Connection.ConnectionString))
            using (SqlCommand cmd = new SqlCommand(@"SELECT p.name, is_output, has_default_value, t.name AS type_name, p.max_length, p.precision, p.scale FROM sys.parameters p JOIN sys.types t ON t.user_type_id = p.system_type_id WHERE object_id = OBJECT_ID(@procname);", cn))
            {
                cn.Open();
                cmd.Parameters.AddWithValue("@procname", incmd.CommandText);

                using (SqlDataReader rdr = cmd.ExecuteReader())
                    while (rdr.Read())
                    {
                        var i = new ProcedureParameter
                        {
                            name = Convert.IsDBNull(rdr["name"]) ? default(String) : Convert.ToString(rdr["name"]),
                            is_output = Convert.ToBoolean(rdr["is_output"]),
                            has_default_value = Convert.ToBoolean(rdr["has_default_value"]),
                            type_name = Convert.ToString(rdr["type_name"]),
                            max_length = Convert.ToInt16(rdr["max_length"]),
                            precision = Convert.ToByte(rdr["precision"]),
                            scale = Convert.ToByte(rdr["scale"])
                        };
                        l.Add(i);
                    }
            }
            return l;
        }
        public SqlParameter[] AutoParam(object obj, string procname)
        {
            IList<ProcedureParameter> paramlist = GetParamList(obj, procname);

            var sqlparams = new List<SqlParameter>();

            foreach (var item in paramlist)
            {
                var value = item.ObjectProperty.GetValue(obj);
                //2024-10-26: using GetSqlDbType instead of auto-detecting the type with just name/value because length/size was being set to 0 particularly when there was no value
                SqlParameter p = new SqlParameter(item.name, item.GetSqlDbType(), item.max_length);
                if (value == null)
                {
                    //p = new SqlParameter(item.name, DBNull.Value);
                    if (!item.ObjectProperty.PropertyType.FullName.Contains("System"))
                    {
                        if (p.SqlDbType != SqlDbType.VarBinary) throw new Exception("why is this not varbinary?");
                    }
                    if (item.ObjectProperty.PropertyType.FullName.Contains("Byte[]"))
                    {
                        if (p.SqlDbType != SqlDbType.Binary) throw new Exception("why is this not binary?");
                    }
                    p.Value = DBNull.Value;
                }
                else if (MDDFoundation.Foundation.IsDateTimeType(item.ObjectProperty.PropertyType) && (DateTime)value == DateTime.MinValue)
                {
                    p.Value = DBNull.Value;
                }
                else
                {
                    //p = new SqlParameter(item.name, value);
                    p.Value = value;
                }
                if (item.is_output) p.Direction = ParameterDirection.InputOutput;
                sqlparams.Add(p);
            }
            return sqlparams.ToArray();
        }
        private IList<ProcedureParameter> GetParamList(object obj, string procname)
        {
            var key = $"{procname}~{obj.GetType().Name}";

            //ProcedureParameter.ParamLists.Clear();


            return ProcedureParameter.ParamLists.GetOrAdd(key, (keyval) =>
            {
                var procparams = ProcedureParameterList(procname);
                foreach (var item in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    string name = item.Name;
                    foreach (var attr in item.GetCustomAttributes(true))
                    {
                        if (attr is DBNameAttribute) name = (attr as DBNameAttribute).DBName;
                    }
                    var pp = procparams.FirstOrDefault(x => x.name.Equals("@" + name, StringComparison.OrdinalIgnoreCase));
                    if (pp != null)
                        pp.ObjectProperty = item;
                }
                return procparams.Where(x => x.ObjectProperty != null).ToList();
            });
        }
        public void ProcessOutputParameters(object obj, string procname, SqlParameter[] parameters)
        {
            var key = $"{procname}~{obj.GetType().Name}";
            if (ProcedureParameter.ParamLists.TryGetValue(key,out IList<ProcedureParameter> procparams))
            {
                foreach (var pp in procparams.Where(x => x.is_output))
                {
                    var sqlp = parameters.FirstOrDefault(x => x.ParameterName.Equals(pp.name));
                    if (sqlp != null)
                        pp.ObjectProperty.SetValue(obj, sqlp.Value);
                }



                //foreach (SqlParameter p in parameters)
                //{
                //    if (p.Direction == ParameterDirection.InputOutput || p.Direction == ParameterDirection.Output)
                //    {
                //        var pp = procparams.FirstOrDefault(x => x.name.Equals(p.ParameterName.Substring(1), StringComparison.OrdinalIgnoreCase));
                //        if (pp != null)
                //            pp.ObjectProperty.SetValue(obj, p.Value);
                //    }
                //}
            }
        }
        /// <summary>
        /// This just looks for a parameterless SQL procedure with the name of the generic type and "_Select", runs it and returns the results
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public IList<T> ListOf<T>() where T: class, new()
        {
            var procname = $"{typeof(T).Name}_Select";
            var selectattr = typeof(T).GetCustomAttribute<DBSelectAttribute>();
            if (selectattr != null) procname = selectattr.SelectProcName;
            return SqlRunQueryWithResults<T>(procname, true, -1, null);
        }
        public async Task<IList<T>> ListOfAsync<T>(CancellationToken token) where T: class, new()
        {
            var procname = $"{typeof(T).Name}_Select";
            var selectattr = typeof(T).GetCustomAttribute<DBSelectAttribute>();
            if (selectattr != null) procname = selectattr.SelectProcName;
            return await SqlRunQueryWithResultsAsync<T>(procname, true, token, -1, null).ConfigureAwait(false);
        }


        public IList<T> ListBy<T>(object obj) where T: class, new()
        {
            var procname = $"{typeof(T).Name}_Select";
            var selectattr = typeof(T).GetCustomAttribute<DBSelectByAttribute>();
            if (selectattr != null) procname = selectattr.SelectProcName;
            return SqlRunQueryWithResults<T>(procname, true, -1, null, AutoParam(obj, procname));
        }
        public async Task<IList<T>> ListByAsync<T>(object obj) where T: class, new()
        {
            var procname = $"{typeof(T).Name}_Select";
            var selectattr = typeof(T).GetCustomAttribute<DBSelectByAttribute>();
            if (selectattr != null) procname = selectattr.SelectProcName;
            return await SqlRunQueryWithResultsAsync<T>(procname, true, CancellationToken.None, -1, null, AutoParam(obj, procname)).ConfigureAwait(false);
        }
        public void DBDelete(object obj)
        {
            string procname = $"{obj.GetType().Name}_Delete";
            var deleteattr = obj.GetType().GetCustomAttribute<DBDeleteAttribute>();
            if (deleteattr != null) procname = deleteattr.DeleteProcName;
            SqlRunProcedure(procname, obj, -1, null);
        }
        public void DBUpsert<T>(T obj) where T: class, new()
        {
            string procname = $"{obj.GetType().Name}_Upsert";
            var upsertattr = obj.GetType().GetCustomAttribute<DBUpsertAttribute>();
            if (upsertattr != null) procname = upsertattr.UpsertProcName;
            RunSqlUpdate(obj, procname, -1, null);
        }
    }
}
