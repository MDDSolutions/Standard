using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MDDDataAccess
{
    public partial class DBEngine
    {

        #region Procedures And Statements
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
                            await ExecuteNonQueryAsync(cmd, CancellationToken).ConfigureAwait(false);
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
                            PrintExecStatement(cmd);
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
                            var t = ExecuteNonQueryAsync(cmd, token);
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
        #endregion

        #region Generics With Results
        public async Task<List<T>> SqlRunQueryWithResultsAsync<T>(string cmdtext, bool IsProcedure, CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list) where T: class, new()
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

                        var l = new List<T>();
                        List<PropertyMapEntry> map = null;
                        Tracker<T> t = Tracking != ObjectTracking.None ? GetTracker<T>() : null;
                        PropertyInfo key = null;
                        try
                        {
                            using (SqlDataReader rdr = await ExecuteReaderAsync(cmd, CancellationToken).ConfigureAwait(false))
                            {
                                //while (await rdr.ReadAsync(CancellationToken).ConfigureAwait(false))
                                while (rdr.Read())
                                {
                                    rowcount++;
                                    T r = null;
                                    ObjectFromReader<T>(rdr, ref map, ref key, ref r, ref t);
                                    l.Add(r);
                                    if (CancellationToken.IsCancellationRequested) return l;
                                }
                            }
                            return l;
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"SqlRunQueryWithResultsAsync running {cmd.Details()}: error on record {rowcount}", ex);
                        }
                    }
                }
            }
            return null;
        }
        public async Task<List<T>> SqlRunQueryWithResultsStubAsync<T>(string cmdtext, bool IsProcedure, CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params ParameterStub[] list) where T: class, new()
        {
            return await SqlRunQueryWithResultsAsync<T>(cmdtext, IsProcedure, CancellationToken, ConnectionTimeout, ApplicationName, StubsToSqlParameters(list)).ConfigureAwait(false);
        }
        public IList<T> SqlRunQueryWithResults<T>(string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list) where T : class, new()
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
                            List<PropertyMapEntry> map = null;
                            Tracker<T> t = Tracking != ObjectTracking.None && Tracked<T>.IsTrackable ? GetTracker<T>() : null;

                            using (SqlDataReader rdr = ExecuteReader(cmd))
                            {
                                while (rdr.Read())
                                {
                                    T r = null;
                                    ObjectFromReader<T>(rdr, ref map, ref key, ref r, ref t);
                                    l.Add(r);

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


        public IList<T> SqlRunQueryWithResultsWithMetrics<T>(string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list) where T : class, new()
        {
            QueryExecutionMetrics metrics = new QueryExecutionMetrics();
            List<T> l = null;

            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");
            using (metrics.MeasureConnection())
            using (var cn = getconnection(ConnectionTimeout, ApplicationName))
            {
                if (cn != null)
                {
                    using (metrics.MeasureCommand())
                    using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);
                        try
                        {
                            l = new List<T>();
                            PropertyInfo key = null;
                            List<PropertyMapEntry> map = null;
                            Tracker<T> t = Tracking != ObjectTracking.None ? GetTracker<T>() : null;
                            using (metrics.MeasureReaderOpen())
                            using (SqlDataReader rdr = ExecuteReader(cmd))
                            using (metrics.MeasureHydration())
                            {
                                while (rdr.Read())
                                {
                                    T r = null;
                                    ObjectFromReaderWithMetrics<T>(rdr, ref map, ref key, ref r, ref t, true, metrics);
                                    l.Add(r);
                                    metrics.IncrementRowCount();
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
            if (KeepStats)
            {
                new CommandExecutionLog
                {
                    ExecDateTime = DateTime.Now,
                    ObjectType = typeof(T).Name,
                    ExecCommand = cmdtext,
                    QueryRowCount = metrics.Rows,
                    ConnectionTime = Convert.ToSingle(metrics.ConnectionTime) / 10000f,
                    CommandTime = Convert.ToSingle(metrics.CommandPreparationTime) / 10000f,
                    ReaderTime = Convert.ToSingle(metrics.ReaderOpenTime) / 10000f,
                    MapBuildTime = Convert.ToSingle(metrics.MapBuildTime) / 10000f,
                    HydrationTime = Convert.ToSingle(metrics.HydrationTime) / 10000f,
                    TrackerTime = Convert.ToSingle(metrics.TrackerProcessingTime) / 10000f
                }.DBUpsert(this);
            }
            return l;
        }



        public IList<T> SqlRunQueryWithResultsStub<T>(string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params ParameterStub[] list) where T: class, new()
        {
            return SqlRunQueryWithResults<T>(cmdtext, IsProcedure, ConnectionTimeout, ApplicationName, StubsToSqlParameters(list));
        }
        public void SqlRunQueryRowByRow<T>(string cmdtext, Func<T, int, bool> rowcallback, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list) where T: class, new()
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
                            PropertyInfo key = null;
                            List<PropertyMapEntry> map = null;
                            Tracker<T> t = Tracking != ObjectTracking.None ? GetTracker<T>() : null;
                            int rowindex = 0;
                            using (SqlDataReader rdr = ExecuteReader(cmd))
                            {
                                while (rdr.Read())
                                {
                                    rowindex++;
                                    T r = null;
                                    ObjectFromReader<T>(rdr, ref map, ref key, ref r, ref t);
                                    if (!rowcallback(r, rowindex)) break;
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
        }
        public delegate Task RowCallback<T>(T entity, int rowIndex, CancellationToken cancellationToken, int workerId);
        public async Task SqlRunQueryRowByRowAsync_old<T>(string cmdtext, RowCallback<T> rowcallback, bool IsProcedure, CancellationToken cancellationToken, int parallelcallbacks = 5, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list) where T: class, new()
        {
            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");
            using (var cn = await getconnectionasync(cancellationToken, ConnectionTimeout, ApplicationName).ConfigureAwait(false))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);
                        Tuple<T, Task>[] tasks = new Tuple<T, Task>[parallelcallbacks];
                        for (int i = 0; i < tasks.Length; i++)
                            tasks[i] = new Tuple<T, Task>(default, Task.CompletedTask);
                        try
                        {
                            PropertyInfo key = null;
                            List<PropertyMapEntry> map = null;
                            Tracker<T> t = Tracking != ObjectTracking.None ? GetTracker<T>() : null;
                            int rowindex = 0;

                            using (SqlDataReader rdr = await ExecuteReaderAsync(cmd, cancellationToken).ConfigureAwait(false))
                            {
                                while (rdr.Read())
                                {
                                    rowindex++;

                                    var availabletask = await Task.WhenAny(tasks.Where(x => x != null).Select(x => x.Item2)).ConfigureAwait(false);
                                    var taskindex = Array.FindIndex(tasks, x => x.Item2 == availabletask);

                                    T r = null;
                                    ObjectFromReader<T>(rdr, ref map, ref key, ref r, ref t);
                                    tasks[taskindex] = new Tuple<T, Task>(r, rowcallback(r, rowindex, cancellationToken, taskindex));

                                    if (cancellationToken.IsCancellationRequested) break;
                                }
                            }
                            await Task.WhenAll(tasks.Where(x => x != null).Select(x => x.Item2)).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            throw ex;
                        }
                    }
                }
            }
        }
        public async Task SqlRunQueryRowByRowAsync<T>(
                string cmdtext,
                RowCallback<T> rowcallback,
                bool IsProcedure,
                CancellationToken cancellationToken,
                int parallelcallbacks = 5,
                int ConnectionTimeout = -1,
                string ApplicationName = null,
                params SqlParameter[] list
            ) where T : class, new()
        {
            if (!IsProcedure && !AllowAdHoc)
                throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");

            using (var cn = await getconnectionasync(cancellationToken, ConnectionTimeout, ApplicationName).ConfigureAwait(false))
            {
                using (var cmd = new SqlCommand(cmdtext, cn))
                {
                    cmd.CommandTimeout = CommandTimeout;
                    if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                    ParameterizeCommand(list, cmd);

                    var tasks = new Task[parallelcallbacks];
                    int rowindex = 0;
                    PropertyInfo key = null;
                    List<PropertyMapEntry> map = null;
                    Tracker<T> t = Tracking != ObjectTracking.None ? GetTracker<T>() : null;

                    using (var rdr = await ExecuteReaderAsync(cmd, cancellationToken).ConfigureAwait(false))
                    {
                        while (rdr.Read())
                        {
                            rowindex++;

                            // build the row object
                            T r = null;
                            ObjectFromReader<T>(rdr, ref map, ref key, ref r, ref t);

                            // look for a free slot
                            int slot = -1;
                            for (int i = 0; i < tasks.Length; i++)
                            {
                                if (tasks[i] == null || tasks[i].IsCompleted)
                                {
                                    slot = i;
                                    break;
                                }
                            }

                            // if all busy, wait for one to complete
                            if (slot == -1)
                            {
                                var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
                                slot = Array.IndexOf(tasks, finished);
                            }

                            // assign work into that slot
                            tasks[slot] = rowcallback(r, rowindex, cancellationToken, slot);

                            if (cancellationToken.IsCancellationRequested)
                                break;
                        }
                    }
                    await Task.WhenAll(tasks.Where(tk => tk != null)).ConfigureAwait(false);
                }
            }
        }


        public Tuple<List<T>, List<R>> SqlRunQueryWithResults<T, R>(string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
            where T: class, new()
            where R : class, new()
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
                                List<PropertyMapEntry> map = null;
                                Tracker<T> tt = Tracking != ObjectTracking.None ? GetTracker<T>() : null;
                                Tracker<R> tr = Tracking != ObjectTracking.None ? GetTracker<R>() : null;
                                while (rdr.Read())
                                {
                                    T tc = null;
                                    ObjectFromReader(rdr, ref map, ref key, ref tc, ref tt);
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
                                        R rc = null;
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
        public async Task<Tuple<List<T>, List<R>>> SqlRunQueryWithResultsAsync<T, R>(string cmdtext, bool IsProcedure, CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
            where T: class, new()
            where R : class, new()
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
                            using (SqlDataReader rdr = await ExecuteReaderAsync(cmd, CancellationToken).ConfigureAwait(false))
                            {
                                PropertyInfo key = null;
                                List<PropertyMapEntry> map = null;
                                Tracker<T> tt = Tracking != ObjectTracking.None ? GetTracker<T>() : null;
                                Tracker<R> tr = Tracking != ObjectTracking.None ? GetTracker<R>() : null;
                                while (rdr.Read())
                                {
                                    T tc = null;
                                    ObjectFromReader(rdr, ref map, ref key, ref tc, ref tt);
                                    t.Add(tc);
                                }
                                await rdr.NextResultAsync().ConfigureAwait(false);
                                if (rdr.HasRows)
                                {
                                    map = null;
                                    key = null;
                                    tr = null;
                                    while (rdr.Read())
                                    {
                                        R rc = null;
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
        public async Task<Tuple<List<T>, List<R>>> SqlRunQueryWithResultsStubAsync<T, R>(string cmdtext, bool IsProcedure, CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params ParameterStub[] list)
            where T: class, new()
            where R : class, new()
        {
            return await SqlRunQueryWithResultsAsync<T, R>(cmdtext, IsProcedure, CancellationToken, ConnectionTimeout, ApplicationName, StubsToSqlParameters(list)).ConfigureAwait(false);
        }
        public async Task<T> SqlRunQueryHeaderDetailAsync<T, R>(string cmdtext, bool IsProcedure, CancellationToken CancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
            where T: class, new()
            where R : class, new()
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
                            T h = null;
                            using (SqlDataReader rdr = await ExecuteReaderAsync(cmd, CancellationToken).ConfigureAwait(false))
                            {
                                bool found = false;
                                //List<Tuple<PropertyInfo, String>> map = null;
                                PropertyInfo key = null;
                                List<PropertyMapEntry> map = null;
                                Tracker<T> tt = Tracking != ObjectTracking.None ? GetTracker<T>() : null;
                                Tracker<R> tr = Tracking != ObjectTracking.None ? GetTracker<R>() : null;
                                while (rdr.Read())
                                {
                                    if (found) throw new Exception("Only one record expected in the header result");
                                    ObjectFromReader<T>(rdr, ref map, ref key, ref h, ref tt);
                                    found = true;
                                }
                                await rdr.NextResultAsync().ConfigureAwait(false);
                                if (rdr.HasRows)
                                {
                                    var l = new List<R>();
                                    map = null;
                                    key = null;
                                    tr = null;
                                    while (rdr.Read())
                                    {
                                        R r = null;
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
            where T: class, new()
            where R : class, new()
        {
            return await SqlRunQueryHeaderDetailAsync<T, R>(cmdtext, IsProcedure, CancellationToken, ConnectionTimeout, ApplicationName, StubsToSqlParameters(list)).ConfigureAwait(false);
        }
        public T SqlRunQueryHeaderDetail<T, R>(string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
            where T: class, new()
            where R : class, new()
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
                            T h = null;
                            using (SqlDataReader rdr = ExecuteReader(cmd))
                            {
                                bool found = false;
                                //List<Tuple<PropertyInfo, String>> map = null;
                                PropertyInfo key = null;
                                List<PropertyMapEntry> map = null;
                                Tracker<T> tt = Tracking != ObjectTracking.None ? GetTracker<T>() : null;
                                Tracker<R> tr = Tracking != ObjectTracking.None ? GetTracker<R>() : null;
                                while (rdr.Read())
                                {
                                    if (found) throw new Exception("Only one record expected in the header result");
                                    ObjectFromReader<T>(rdr, ref map, ref key, ref h, ref tt);
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
                                        R r = null;
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
        public IList<T> SqlRunQueryCombined<T, R>(string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
            where T: class, new()
            where R : class, new()
        {
            /* the idea here is that the result set contains both T and R fields in each row but T has a sub-object of type R
             * otherwise we'd return 2 lists or something? we could return a List<Tuple<T,R>> but presumably these objects are related somehow
             * this was developed for an object in which T represents a many-to-many relationship between two other objects and R is one of those objects
             * so T essentially just extends R with a few more properties but R is tracked so we need to use it's pure type
             * so basically run ObjectFromReader twice on the same reader - once for T and once for R and then add R to a property on T that is of type R
             */

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
                        T tobj = default;
                        R robj = default;
                        try
                        {
                            var l = new List<T>();
                            PropertyInfo tkey = null;
                            List<PropertyMapEntry> tmap = null;
                            Tracker<T> ttrk = Tracking != ObjectTracking.None ? GetTracker<T>() : null;

                            PropertyInfo rkey = null;
                            List<PropertyMapEntry> rmap = null;
                            Tracker<R> rtrk = Tracking != ObjectTracking.None ? GetTracker<R>() : null;

                            var subobj = typeof(T).GetPropertyOfType(typeof(R));
                            if (subobj == null)
                                throw new Exception($"The type {typeof(T).FullName} does not have a property that is a {typeof(R).FullName} to hold the sub object");

                            using (SqlDataReader rdr = ExecuteReader(cmd))
                            {
                                while (rdr.Read())
                                {
                                    tobj = null;
                                    ObjectFromReader(rdr, ref tmap, ref tkey, ref tobj, ref ttrk);
                                    l.Add(tobj);

                                    robj = null;
                                    ObjectFromReader(rdr, ref rmap, ref rkey, ref robj, ref rtrk);
                                   
                                    subobj.SetValue(tobj, robj);
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
        #endregion

        #region Generics Update
        bool runsqlupdateinternal<T>(T obj, string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, bool strict = true, params SqlParameter[] list) where T: class, new()
        {
            Tracker<T> t = Tracking != ObjectTracking.None ? GetTracker<T>() : null;
            if (t != null)
            {
                if (!t.TryGet(Tracked<T>.GetKeyValue(obj), out var tracked) || tracked == null)
                    throw new Exception("The object provided for update is trackable but is not currently being tracked - the object was somehow loaded outside the tracking system and so cannot be updated");
            }
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
                                List<PropertyMapEntry> map = null;
                                while (rdr.Read())
                                {
                                    if (found) throw new Exception("Only one record expected in the update result");
                                    ObjectFromReader(rdr, ref map, ref key, ref obj, ref t, strict);
                                    found = true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            DBEngine.PrintExecStatement(cmd);
                            throw ex;
                        }
                    }
                }
            }
            return found;
        }
        public bool RunSqlUpdate<T>(T obj, string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list) where T: class, new()
        {
            return runsqlupdateinternal(obj, cmdtext, IsProcedure, ConnectionTimeout, ApplicationName, true, list);
        }
        public bool RunSqlUpdateNonStrict<T>(T obj, string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list) where T: class, new()
        {
            return runsqlupdateinternal(obj, cmdtext, IsProcedure, ConnectionTimeout, ApplicationName, false, list);
        }
        public bool RunSqlUpdate<T>(T obj, string procName, int ConnectionTimeout = -1, string ApplicationName = null) where T: class, new()
        {
            var plist = AutoParam(obj, procName);
            return RunSqlUpdate(obj, procName, true, ConnectionTimeout, ApplicationName, plist);
        }
        public async Task<bool> RunSqlUpdateAsync<T>(T obj, string cmdtext, bool IsProcedure, CancellationToken token, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list) where T: class, new()
        {

            if (Tracking != ObjectTracking.None && obj is ITrackedEntity ite)
            {
                if (!ite.IsTracked(this))
                    throw new Exception("The object provided for update is an ITrackedEntity and ObjectTracking is enabled - the object was somehow loaded outside the tracking system and so cannot be updated");
            }

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
                            using (SqlDataReader rdr = await ExecuteReaderAsync(cmd, token).ConfigureAwait(false))
                            {
                                if (token.IsCancellationRequested) return false;
                                PropertyInfo key = null;
                                List<PropertyMapEntry> map = null;
                                Tracker<T> t = Tracking != ObjectTracking.None ? GetTracker<T>() : null;
                                while (rdr.Read())
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
        public async Task<bool> RunSqlUpdateAsync<T>(T obj, string procName, CancellationToken token, int ConnectionTimeout = -1, string ApplicationName = null) where T: class, new()
        {
            var plist = AutoParam(obj, procName);
            return await RunSqlUpdateAsync(obj, procName, true, token, ConnectionTimeout, ApplicationName, plist).ConfigureAwait(false);
        }
        public bool RunSqlUpdateHeaderDetail<T, R>(T obj, string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
            where T: class, new()
            where R : class, new()
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
                                List<PropertyMapEntry> map = null;
                                Tracker<T> tt = Tracking != ObjectTracking.None ? GetTracker<T>() : null;
                                Tracker<R> tr = Tracking != ObjectTracking.None ? GetTracker<R>() : null;
                                while (rdr.Read())
                                {
                                    if (found) throw new Exception("Only one record expected in the update result");
                                    ObjectFromReader(rdr, ref map, ref key, ref obj, ref tt);
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
                                        R r = null;
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
        public bool RunSqlUpdateStub<T>(T obj, string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params ParameterStub[] list) where T: class, new()
        {
            return RunSqlUpdate(obj, cmdtext, IsProcedure, ConnectionTimeout, ApplicationName, StubsToSqlParameters(list));
        }
        public bool RunSqlUpdateStub<T>(T obj, string procName, int ConnectionTimeout = -1, string ApplicationName = null) where T: class, new()
        {
            var plist = AutoParam(obj, procName);
            return RunSqlUpdate(obj, procName, true, ConnectionTimeout, ApplicationName, plist);
        }
        #endregion

        #region Dynamics and DataTables
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
        public IList<dynamic> SqlRunQueryWithResultsDynamic(string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
        {
            using (var cn = getconnection(ConnectionTimeout, ApplicationName))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);

                        var results = new List<dynamic>();
                        using (SqlDataReader rdr = ExecuteReader(cmd))
                        {
                            while (rdr.Read())
                            {
                                IDictionary<string, object> row = new ExpandoObject();
                                for (int i = 0; i < rdr.FieldCount; i++)
                                {
                                    var value = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                                    row[rdr.GetName(i)] = value;
                                }
                                results.Add(row);
                            }
                        }
                        return results;
                    }
                }
            }
            return null;
        }
        public async Task<IList<dynamic>> SqlRunQueryWithResultsDynamicAsync(string cmdtext, bool IsProcedure, CancellationToken cancellationToken, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
        {
            using (var cn = await getconnectionasync(cancellationToken, ConnectionTimeout, ApplicationName).ConfigureAwait(false))
            {
                if (cn != null)
                {
                    using (SqlCommand cmd = new SqlCommand(cmdtext, cn))
                    {
                        cmd.CommandTimeout = CommandTimeout;
                        if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                        ParameterizeCommand(list, cmd);

                        var results = new List<dynamic>();
                        using (SqlDataReader rdr = await ExecuteReaderAsync(cmd, cancellationToken).ConfigureAwait(false))
                        {
                            while (rdr.Read())
                            {
                                IDictionary<string, object> row = new ExpandoObject();
                                for (int i = 0; i < rdr.FieldCount; i++)
                                {
                                    var value = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                                    row[rdr.GetName(i)] = value;
                                }
                                results.Add(row);
                            }
                        }
                        return results;
                    }
                }
            }
            return null;
        }
        #endregion
    }
}
