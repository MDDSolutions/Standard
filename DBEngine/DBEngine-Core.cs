using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using System.Data.SqlClient;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text;
using System.Diagnostics;

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
        private async Task<SqlConnection> getconnectionasync(CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null)
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
        public async Task SqlRunProcedureAsync(string procName, CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
        {
            using (var cn = await getconnectionasync(CancellationToken, ConnectionTimeout, ApplicationName).ConfigureAwait(false))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(procName, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);
                        try
                        {
                            await ExecuteNonQueryAsync(cmd,CancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            throw ex;
                        }
                    }
                }
            }
        }
        public async Task SqlRunProcedureStubAsync(string procName, CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params ParameterStub[] list)
        {
            await SqlRunProcedureAsync(procName, CancellationToken, ConnectionTimeout, ApplicationName, StubsToSqlParameters(list)).ConfigureAwait(false);
        }
        public void SqlRunProcedure(string procName, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
        {
            using (var cn = getconnection(ConnectionTimeout, ApplicationName))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(procName, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);
                        try
                        {
                            ExecuteNonQuery(cmd);
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"DBEngine.SqlRunProcedure: Error executing procedure {procName} on server {connectionstring.DataSource}, database {connectionstring.InitialCatalog}: {ex.Message}", ex);
                        }
                    }
                }
            }
        }
        public void SqlRunProcedure(string procName, object obj, int ConnectionTimeout = -1, string ApplicationName = null)
        {
            var plist = AutoParam(obj, procName);
            SqlRunProcedure(procName, ConnectionTimeout, ApplicationName, plist);
            ProcessOutputParameters(obj, procName, plist);
        }
        public void SqlRunProcedureStub(string procName, int ConnectionTimeout = -1, string ApplicationName = null, params ParameterStub[] list)
        {
            SqlRunProcedure(procName, ConnectionTimeout, ApplicationName, StubsToSqlParameters(list));
        }
        public void SqlRunStatement(string cmdText, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
        {
            using (var cn = getconnection(ConnectionTimeout, ApplicationName))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdText, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        ParameterizeCommand(list, cmd);
                        try
                        {
                            ExecuteNonQuery(cmd);
                        }
                        catch (Exception ex)
                        {
                            throw ex;
                        }
                    }
                }
            }
        }
        public async Task SqlRunStatementAsync(string cmdText, CancellationToken token, int ConnectionTimeout = -1, string ApplicationName = null, Action<OperationProgress> progresscallback = null, TimeSpan progressreportinterval = default, Action<string> infomsgcallback = null, params SqlParameter[] list)
        {
            if (ApplicationName == null)
                connectionstring.ApplicationName = DefaultApplicationName;
            if (progressreportinterval == default)
                progressreportinterval = TimeSpan.FromMilliseconds(500);
            ApplicationName = ApplicationName ?? "SqlRunStatementAsync" + "_" + Guid.NewGuid().ToString();
            OperationProgress progress = null;
            using (var cn = getconnection(ConnectionTimeout, ApplicationName))
            {
                if (cn != null)
                {
                    if (infomsgcallback != null)
                    {
                        cn.InfoMessage += (s, e) => 
                        {
                            //this is VERY incomplete - e.Errors[0].Class will be the severity of the "message" and an exception will not be thrown for any severity
                            //you need to see if the "info message" is actually an error and then throw or whatever if it is
                            infomsgcallback(e.Message); 
                        };
                        cn.FireInfoMessageEventOnUserErrors = true;
                    }

                    using (SqlCommand cmd = new SqlCommand(cmdText, cn))
                    {
                        cmd.CommandTimeout = 0; //expected to be long-running - use CancellationToken to cancel if necessary
                        ParameterizeCommand(list, cmd);
                        try
                        {
                            if (progresscallback != null) progress = OperationProgress.StartNew();
                            var t = ExecuteNonQueryAsync(cmd,token);
                            while (!t.IsCompleted)
                            {
                                await Task.Delay(progressreportinterval).ConfigureAwait(false);
                                var allow = AllowAdHoc;
                                AllowAdHoc = true;
                                var der = SqlRunQueryWithResults<DMExecSession>(
                                    "SELECT s.*, r.percent_complete, r.estimated_completion_time FROM sys.dm_exec_sessions s LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id WHERE s.program_name = @ApplicationName",
                                    false, -1, null, GetParameter(() => ApplicationName)).FirstOrDefault();
                                AllowAdHoc = allow;
                                if (der != null && progresscallback != null)
                                {
                                    if (der.percent_complete != null && der.percent_complete != 0)
                                        progress.CurrentStatus = Convert.ToDouble(der.percent_complete);
                                    else
                                        progress.ReportElapsed = true;
                                    progresscallback(progress);
                                }
                            }
                            if (t.Status == TaskStatus.Faulted && t.Exception != null)
                            {

                                throw t.Exception;
                            }
                            if (progresscallback != null)
                            {
                                progress.CurrentStatus = progress.FinalStatus;
                                progresscallback(progress);
                            }
                        }
                        catch (Exception ex)
                        {
                            throw ex;
                        }
                    }
                }
            }
        }
        /// <summary>
        /// BeginExecuteNonQuery has a bug (which is apparently by design - whatever) where if the statement/procedure does *not* have SET NOCOUNT ON *or* there is a RAISERROR "WITH NOWAIT" (and possibly other things)
        /// then the IAsyncResult object will prematurely report "true" for IsCompleted and EndExecuteNonQuery will block - this is really stupid, so try to use the Async method if you can (or test your statement thoroughly)
        /// CheckDB works fine here (though not tested with all options)
        /// </summary>
        /// <param name="cmdText"></param>
        /// <param name="ConnectionTimeout"></param>
        /// <param name="ApplicationName"></param>
        /// <param name="progresscallback"></param>
        /// <param name="progressreportinterval"></param>
        /// <param name="list"></param>
        public void SqlRunLongStatement(string cmdText, int ConnectionTimeout = -1, string ApplicationName = null, Action<OperationProgress> progresscallback = null, TimeSpan progressreportinterval = default, params SqlParameter[] list)
        {
            if (ApplicationName == null)
                connectionstring.ApplicationName = DefaultApplicationName;
            if (progressreportinterval == default)
                progressreportinterval = TimeSpan.FromMilliseconds(500);
            ApplicationName = ApplicationName ?? "SqlRunStatementAsync" + "_" + Guid.NewGuid().ToString();
            OperationProgress progress = null;
            using (var cn = getconnection(ConnectionTimeout, ApplicationName))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdText, cn))
                    {
                        cmd.CommandTimeout = 0; //expected to be long-running - there's a SqlCommand.Cancel method but not sure how you'd run it at this point...
                        ParameterizeCommand(list, cmd);
                        try
                        {
                            if (progresscallback != null) progress = OperationProgress.StartNew();
                            //var t = ExecuteNonQueryAsync(cmd, CancellationToken.None);
                            IAsyncResult t = cmd.BeginExecuteNonQuery();
                            var lastreport = DateTime.Now;
                            bool forcecomplete = false;
                            DMExecSession previousdes = null;
                            DateTime previousdestime = DateTime.MaxValue;
                            while (!t.IsCompleted && !forcecomplete)
                            {
                                //await Task.Delay(progressreportinterval).ConfigureAwait(false);
                                Thread.Sleep(100);
                                if (lastreport.Add(progressreportinterval) <= DateTime.Now)
                                {
                                    var allow = AllowAdHoc;
                                    AllowAdHoc = true;
                                    var des = SqlRunQueryWithResults<DMExecSession>(
                                        "SELECT s.*, r.percent_complete, r.estimated_completion_time FROM sys.dm_exec_sessions s LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id WHERE s.program_name = @ApplicationName",
                                        false, -1, null, GetParameter(() => ApplicationName)).FirstOrDefault();

                                    if (des != null)
                                    {
                                        if (des.status == "sleeping")
                                        {
                                            if (previousdes != null)
                                            {
                                                if (progresscallback != null)
                                                { 
                                                    progress.SpecialStatus = $"process sleeping for {DateTime.Now - previousdestime}";
                                                    progresscallback.Invoke(progress);
                                                }
                                                if (previousdes.cpu_time == des.cpu_time && previousdes.logical_reads == des.logical_reads && previousdes.writes == des.writes)
                                                {
                                                    if ((DateTime.Now - previousdestime) > progressreportinterval)
                                                    {
                                                        if (progresscallback != null)
                                                        {
                                                            progress.SpecialStatus = $"process sleeping for {DateTime.Now - previousdestime} - forcing process completion";
                                                            progresscallback.Invoke(progress);
                                                        }
                                                        forcecomplete = true;
                                                    }
                                                }
                                            }
                                            else if (progresscallback != null)
                                            {
                                                progress.SpecialStatus = null;
                                            }
                                            if (!forcecomplete)
                                            {
                                                previousdes = des;
                                                previousdestime = DateTime.Now;
                                            }
                                        }
                                        else if (progresscallback != null)
                                        {
                                            progress.SpecialStatus = null;
                                        }
                                    }
                                    else
                                    {
                                        if (progresscallback != null)
                                        {
                                            progress.SpecialStatus = "process not found...";
                                            progresscallback.Invoke(progress);
                                        }
                                        if (previousdestime == DateTime.MaxValue)
                                        {
                                            previousdestime = DateTime.Now;
                                        }
                                        else if ((DateTime.Now - previousdestime) > progressreportinterval)
                                        {
                                            if (progresscallback != null)
                                            {
                                                progress.SpecialStatus = $"process not found for {progressreportinterval} - forcing process completion";
                                                progresscallback.Invoke(progress);
                                            }
                                            forcecomplete = true;
                                        }
                                    }

                                    AllowAdHoc = allow;
                                    if (des != null && progresscallback != null)
                                    {
                                        if (des.percent_complete != null && des.percent_complete != 0)
                                        {
                                            progress.ReportElapsed = false;
                                            progress.CurrentStatus = Convert.ToDouble(des.percent_complete);
                                        }
                                        else
                                            progress.ReportElapsed = true;
                                        progresscallback(progress);
                                    }
                                    lastreport = DateTime.Now;
                                }
                            }
                            var r = cmd.EndExecuteNonQuery(t);
                            if (progresscallback != null)
                            {
                                progress.CurrentStatus = progress.FinalStatus;
                                progresscallback(progress);
                            }
                        }
                        catch (Exception ex)
                        {
                            throw ex;
                        }
                    }
                }
            }
        }
        public async Task<List<T>> SqlRunQueryWithResultsAsync<T>(string cmdtext, bool IsProcedure, CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list) where T : new()
        {
            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");
            using (var cn = await getconnectionasync(CancellationToken, ConnectionTimeout, ApplicationName).ConfigureAwait(false))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);
                        int rowcount = 0;
                        //T r = default;

                        var l = new List<T>();
                        //List<Tuple<PropertyInfo, String>> map = null;
                        List<Tuple<Action<object, object>, String>> map = null;
                        IObjectTracker t = null;
                        PropertyInfo key = null;
                        using (SqlDataReader rdr = await ExecuteReaderAsync(cmd,CancellationToken).ConfigureAwait(false))
                        {
                            while (await rdr.ReadAsync(CancellationToken).ConfigureAwait(false))
                            {
                                try
                                {                                    
                                    rowcount++;
                                    var r = new T();
                                    ObjectFromReader<T>(rdr, ref map, ref key, ref r, ref t);
                                    l.Add(r);
                                    if (CancellationToken.IsCancellationRequested) return l;
                                }
                                catch (Exception ex)
                                {
                                    throw new Exception($"SqlRunQueryWithResultsAsync running {cmd.Details()}: error on record {rowcount}", ex);
                                }                                
                            }
                        }
                        return l;
                    }
                }
            }
            return null;
        }
        public async Task<List<T>> SqlRunQueryWithResultsStubAsync<T>(string cmdtext, bool IsProcedure, CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params ParameterStub[] list) where T : new()
        {
            return await SqlRunQueryWithResultsAsync<T>(cmdtext, IsProcedure, CancellationToken, ConnectionTimeout, ApplicationName, StubsToSqlParameters(list)).ConfigureAwait(false);
        }
        public IEnumerable<IDataRecord> SqlRunQueryWithResultsDataRecord(string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
        {
            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");
            using (var cn = getconnection(ConnectionTimeout, ApplicationName))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);
                        using (SqlDataReader rdr = ExecuteReader(cmd))
                            foreach (IDataRecord item in rdr as IEnumerable)
                                yield return item;
                    }
                }
            }
        }
        //public DataTable SqlRunQueryWithResultsDataTable(string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
        //{
        //    if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");
        //    using (var cn = getconnection(ConnectionTimeout, ApplicationName))
        //    {
        //        if (cn != null)
        //        {
        //            var dt = new DataTable();
        //            using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
        //            {
        //                cmd.CommandTimeout = CommandTimeout;
        //                if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
        //                ParameterizeCommand(list, cmd);
        //                using (SqlDataReader rdr = ExecuteReader(cmd))
        //                {
        //                    for (int i = 0; i < rdr.FieldCount; i++)
        //                    {
        //                        dt.Columns.Add(rdr.GetName(i));
        //                    }
        //                    while (rdr.Read())
        //                    {
        //                        var items = new string[rdr.FieldCount];
        //                        for (int i = 0; i < rdr.FieldCount; i++)
        //                        {
        //                            items[i] = rdr.GetValue(i).ToString();
        //                        }
        //                        dt.Rows.Add(items);
        //                    }
        //                }
        //            }
        //            return dt;
        //        }
        //    }
        //    throw new Exception("Something went wrong");
        //}
        public DataTable SqlRunQueryWithResultsDataTable(string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
        {
            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");

            using (var cn = getconnection(ConnectionTimeout, ApplicationName))
            {
                if (cn != null)
                {
                    var dt = new DataTable();
                    using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);

                        using (SqlDataAdapter adapter = new SqlDataAdapter(cmd))
                        {
                            adapter.Fill(dt);
                        }
                    }
                    return dt;
                }
            }
            throw new Exception("Something went wrong");
        }
        public async Task<DataTable> SqlRunQueryWithResultsDataTableAsync(string cmdtext, bool IsProcedure, CancellationToken cancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
        {
            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");

            using (var cn = await getconnectionasync(cancellationToken, ConnectionTimeout, ApplicationName).ConfigureAwait(false))
            {
                if (cn != null)
                {
                    var dt = new DataTable();
                    using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);

                        using (SqlDataAdapter adapter = new SqlDataAdapter(cmd))
                        {
                            await Task.Run(() => adapter.Fill(dt), cancellationToken).ConfigureAwait(false);
                        }
                    }
                    return dt;
                }
            }
            throw new Exception("Something went wrong");
        }

        public IList<T> SqlRunQueryWithResults<T>(string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list) where T : new()
        {
            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");
            using (var cn = getconnection(ConnectionTimeout, ApplicationName))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);
                        try
                        {
                            var l = new List<T>();
                            PropertyInfo key = null;
                            //List<Tuple<PropertyInfo, String>> map = null;
                            List<Tuple<Action<object, object>, String>> map = null;
                            IObjectTracker t = null;
                            using (SqlDataReader rdr = ExecuteReader(cmd))
                            {
                                while (rdr.Read())
                                {
                                    //l.Add((T)Activator.CreateInstance(typeof(T), rdr));
                                    var r = new T();
                                    ObjectFromReader<T>(rdr, ref map, ref key, ref r, ref t);
                                    l.Add(r);
                                    //l.Add(ObjectFromReader<T>(rdr));
                                }
                            }
                            return l;
                        }
                        catch (Exception ex)
                        {
                            throw ex;
                        }
                    }
                }
            }
            return null;
        }
        public IList<T> SqlRunQueryWithResultsStub<T>(string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params ParameterStub[] list) where T: new()
        {
            return SqlRunQueryWithResults<T>(cmdtext, IsProcedure, ConnectionTimeout, ApplicationName, StubsToSqlParameters(list));
        }
        public async Task<Tuple<List<T>, List<R>>> SqlRunQueryWithResultsAsync<T, R>(string cmdtext, bool IsProcedure, CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
            where T : new()
            where R : new()
        {
            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");
            var t = new List<T>();
            var r = new List<R>();
            using (var cn = await getconnectionasync(CancellationToken, ConnectionTimeout, ApplicationName).ConfigureAwait(false))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);
                        try
                        {
                            using (SqlDataReader rdr = await ExecuteReaderAsync(cmd,CancellationToken).ConfigureAwait(false))
                            {
                                PropertyInfo key = null;
                                List<Tuple<Action<object, object>, String>> map = null;
                                IObjectTracker tr = null;
                                while (await rdr.ReadAsync().ConfigureAwait(false))
                                {
                                    var tc = new T();
                                    ObjectFromReader(rdr, ref map, ref key, ref tc, ref tr);
                                    t.Add(tc);
                                }
                                await rdr.NextResultAsync().ConfigureAwait(false);
                                if (rdr.HasRows)
                                {
                                    map = null;
                                    key = null;
                                    tr = null;
                                    while (await rdr.ReadAsync().ConfigureAwait(false))
                                    {
                                        var rc = new R();
                                        ObjectFromReader(rdr, ref map, ref key, ref rc, ref tr);
                                        r.Add(rc);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            throw ex;
                        }
                    }
                }
            }
            return new Tuple<List<T>, List<R>>(t, r);
        }
        public Tuple<List<T>, List<R>> SqlRunQueryWithResults<T, R>(string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
            where T : new()
            where R : new()
        {
            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");
            var t = new List<T>();
            var r = new List<R>();
            using (var cn = getconnection(ConnectionTimeout, ApplicationName))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);
                        try
                        {
                            using (SqlDataReader rdr = ExecuteReader(cmd))
                            {
                                PropertyInfo key = null;
                                List<Tuple<Action<object, object>, String>> map = null;
                                IObjectTracker tr = null;
                                while (rdr.Read())
                                {
                                    var tc = new T();
                                    ObjectFromReader(rdr, ref map, ref key, ref tc, ref tr);
                                    t.Add(tc);
                                }
                                rdr.NextResult();
                                if (rdr.HasRows)
                                {
                                    map = null;
                                    key = null;
                                    tr = null;
                                    while (rdr.Read())
                                    {
                                        var rc = new R();
                                        ObjectFromReader(rdr, ref map, ref key, ref rc, ref tr);
                                        r.Add(rc);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            throw ex;
                        }
                    }
                }
            }
            return new Tuple<List<T>, List<R>>(t, r);
        }
        public async Task<Tuple<List<T>,List<R>>> SqlRunQueryWithResultsStubAsync<T,R>(string cmdtext, bool IsProcedure, CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params ParameterStub[] list) 
            where T : new()
            where R : new()
        {
            return await SqlRunQueryWithResultsAsync<T,R>(cmdtext, IsProcedure, CancellationToken, ConnectionTimeout, ApplicationName, StubsToSqlParameters(list)).ConfigureAwait(false);
        }
        public async Task<T> SqlRunQueryHeaderDetailAsync<T, R>(string cmdtext, bool IsProcedure, CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
            where T : new()
            where R : new()
        {
            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");
            using (var cn = await getconnectionasync(CancellationToken, ConnectionTimeout, ApplicationName).ConfigureAwait(false))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);
                        try
                        {
                            T h = default(T);
                            using (SqlDataReader rdr = await ExecuteReaderAsync(cmd,CancellationToken).ConfigureAwait(false))
                            {
                                bool found = false;
                                //List<Tuple<PropertyInfo, String>> map = null;
                                PropertyInfo key = null;
                                List<Tuple<Action<object, object>, String>> map = null;
                                IObjectTracker tr = null;
                                while (await rdr.ReadAsync().ConfigureAwait(false))
                                {
                                    if (found) throw new Exception("Only one record expected in the header result");
                                    h = new T();
                                    ObjectFromReader<T>(rdr, ref map, ref key, ref h, ref tr);
                                    found = true;
                                }
                                await rdr.NextResultAsync().ConfigureAwait(false);
                                if (rdr.HasRows)
                                {
                                    var l = new List<R>();
                                    map = null;
                                    key = null;
                                    tr = null;
                                    while (await rdr.ReadAsync().ConfigureAwait(false))
                                    {
                                        var r = new R();
                                        ObjectFromReader<R>(rdr, ref map, ref key, ref r, ref tr);
                                        l.Add(r);
                                    }
                                    var lst = h.GetDetailListProperty(l.GetType());
                                    if (lst != null)
                                        lst.SetValue(h, l);
                                }
                            }
                            return h;
                        }
                        catch (Exception ex)
                        {
                            throw ex;
                        }
                    }
                }
            }
            return default(T);
        }
        public async Task<T> SqlRunQueryHeaderDetailStubAsync<T, R>(string cmdtext, bool IsProcedure, CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params ParameterStub[] list)
            where T : new()
            where R : new()
        {
            return await SqlRunQueryHeaderDetailAsync<T, R>(cmdtext, IsProcedure, CancellationToken, ConnectionTimeout, ApplicationName, StubsToSqlParameters(list)).ConfigureAwait(false);
        }
        public T SqlRunQueryHeaderDetail<T, R>(string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
            where T : new()
            where R : new()
        {
            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");
            using (var cn = getconnection(ConnectionTimeout, ApplicationName))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);
                        try
                        {
                            T h = default(T);
                            using (SqlDataReader rdr = ExecuteReader(cmd))
                            {
                                bool found = false;
                                //List<Tuple<PropertyInfo, String>> map = null;
                                PropertyInfo key = null;
                                List<Tuple<Action<object, object>, String>> map = null;
                                IObjectTracker tr = null;
                                while (rdr.Read())
                                {
                                    if (found) throw new Exception("Only one record expected in the header result");
                                    ObjectFromReader<T>(rdr, ref map, ref key, ref h, ref tr);
                                    found = true;
                                }
                                rdr.NextResult();
                                if (rdr.HasRows)
                                {
                                    var l = new List<R>();
                                    map = null;
                                    key = null;
                                    tr = null;
                                    while (rdr.Read())
                                    {
                                        var r = new R();
                                        ObjectFromReader<R>(rdr, ref map, ref key, ref r, ref tr);
                                        l.Add(r);
                                    }
                                    var lst = h.GetDetailListProperty(l.GetType());
                                    if (lst != null)
                                        lst.SetValue(h, l);
                                }
                            }
                            return h;
                        }
                        catch (Exception ex)
                        {
                            throw ex;
                        }
                    }
                }
            }
            return default(T);
        }
        public bool RunSqlUpdate<T>(T obj, string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list) where T : new()
        {
            bool found = false;
            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");
            using (var cn = getconnection(ConnectionTimeout, ApplicationName))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);
                        try
                        {
                            using (SqlDataReader rdr = ExecuteReader(cmd))
                            {
                                PropertyInfo key = null;
                                List<Tuple<Action<object, object>, String>> map = null;
                                IObjectTracker t = null;
                                while (rdr.Read())
                                {
                                    if (found) throw new Exception("Only one record expected in the update result");
                                    ObjectFromReader(rdr, ref map, ref key, ref obj, ref t);
                                    found = true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            throw ex;
                        }
                    }
                }
            }
            return found;
        }
        public bool RunSqlUpdate<T>(T obj, string procName, int ConnectionTimeout = -1, string ApplicationName = null) where T : new()
        {
            var plist = AutoParam(obj, procName);
            return RunSqlUpdate(obj, procName, true, ConnectionTimeout, ApplicationName, plist);
        }
        public async Task<bool> RunSqlUpdateAsync<T>(T obj, string cmdtext, bool IsProcedure, CancellationToken token, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list) where T : new()
        {
            bool found = false;
            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");
            using (var cn = getconnection(ConnectionTimeout, ApplicationName))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);
                        try
                        {
                            using (SqlDataReader rdr = await ExecuteReaderAsync(cmd,token).ConfigureAwait(false))
                            {
                                if (token.IsCancellationRequested) return false;
                                PropertyInfo key = null;
                                List<Tuple<Action<object, object>, String>> map = null;
                                IObjectTracker t = null;
                                while (await rdr.ReadAsync(token).ConfigureAwait(false))
                                {
                                    if (token.IsCancellationRequested) return false;
                                    if (found) throw new Exception("Only one record expected in the update result");
                                    ObjectFromReader(rdr, ref map, ref key, ref obj, ref t);
                                    found = true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            throw ex;
                        }
                    }
                }
            }
            return found;
        }
        public async Task<bool> RunSqlUpdateAsync<T>(T obj, string procName, CancellationToken token, int ConnectionTimeout = -1,string ApplicationName = null) where T : new()
        {
            var plist = AutoParam(obj, procName);
            return await RunSqlUpdateAsync(obj, procName, true, token, ConnectionTimeout, ApplicationName, plist).ConfigureAwait(false);
        }
        public bool RunSqlUpdateHeaderDetail<T,R>(T obj, string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
            where T : new()
            where R : new()
        {
            bool found = false;
            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");
            using (var cn = getconnection(ConnectionTimeout, ApplicationName))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);
                        try
                        {
                            using (SqlDataReader rdr = ExecuteReader(cmd))
                            {
                                PropertyInfo key = null;
                                List<Tuple<Action<object, object>, String>> map = null;
                                IObjectTracker tr = null;
                                while (rdr.Read())
                                {
                                    if (found) throw new Exception("Only one record expected in the update result");
                                    ObjectFromReader(rdr, ref map, ref key, ref obj, ref tr);
                                    found = true;
                                }
                                rdr.NextResult();
                                if (rdr.HasRows)
                                {
                                    var l = new List<R>();
                                    map = null;
                                    key = null;
                                    tr = null;
                                    while (rdr.Read())
                                    {
                                        var r = new R();
                                        ObjectFromReader<R>(rdr, ref map, ref key, ref r, ref tr);
                                        l.Add(r);
                                    }
                                    var lst = obj.GetDetailListProperty(l.GetType());
                                    if (lst != null)
                                        lst.SetValue(obj, l);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            throw ex;
                        }
                    }
                }
            }
            return found;
        }
        public bool RunSqlUpdateStub<T>(T obj, string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params ParameterStub[] list) where T : new()
        {
            return RunSqlUpdate(obj, cmdtext, IsProcedure, ConnectionTimeout, ApplicationName, StubsToSqlParameters(list));
        }
        public bool RunSqlUpdateStub<T>(T obj, string procName, int ConnectionTimeout = -1, string ApplicationName = null) where T : new()
        {
            var plist = AutoParam(obj, procName);
            return RunSqlUpdate(obj, procName, true, ConnectionTimeout, ApplicationName, plist);
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
                SqlParameter p;
                if (value == null)
                {
                    p = new SqlParameter(item.name, DBNull.Value);
                    if (!item.ObjectProperty.PropertyType.FullName.Contains("System"))
                        p.SqlDbType = SqlDbType.VarBinary;
                    if (item.ObjectProperty.PropertyType.FullName.Contains("Byte[]"))
                        p.SqlDbType = SqlDbType.Binary;
                }
                else
                {
                    p = new SqlParameter(item.name, value);
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
        public IList<T> ListOf<T>() where T : new()
        {
            var procname = $"{typeof(T).Name}_Select";
            var selectattr = typeof(T).GetCustomAttribute<DBSelectAttribute>();
            if (selectattr != null) procname = selectattr.SelectProcName;
            return SqlRunQueryWithResults<T>(procname, true, -1, null);
        }
        public async Task<IList<T>> ListOfAsync<T>(CancellationToken token) where T : new()
        {
            var procname = $"{typeof(T).Name}_Select";
            var selectattr = typeof(T).GetCustomAttribute<DBSelectAttribute>();
            if (selectattr != null) procname = selectattr.SelectProcName;
            return await SqlRunQueryWithResultsAsync<T>(procname, true, token, -1, null).ConfigureAwait(false);
        }


        public IList<T> ListBy<T>(object obj) where T : new()
        {
            var procname = $"{typeof(T).Name}_Select";
            var selectattr = typeof(T).GetCustomAttribute<DBSelectByAttribute>();
            if (selectattr != null) procname = selectattr.SelectProcName;
            return SqlRunQueryWithResults<T>(procname, true, -1, null, AutoParam(obj, procname));
        }
        public async Task<IList<T>> ListByAsync<T>(object obj) where T : new()
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
        public void DBUpsert<T>(T obj) where T : new()
        {
            string procname = $"{obj.GetType().Name}_Upsert";
            var upsertattr = obj.GetType().GetCustomAttribute<DBUpsertAttribute>();
            if (upsertattr != null) procname = upsertattr.UpsertProcName;
            RunSqlUpdate(obj, procname, -1, null);
        }
    }
}
