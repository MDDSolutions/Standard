using System;
using System.ComponentModel;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace MDDDataAccess
{
    // 2026-09-24: connection health.
    //
    // DBConnected used to mean "the last Open() succeeded". A pooled connection opens without a round
    // trip, so a dead network path could keep DBConnected true for days while every command failed, and
    // nothing in the application ever tried to recover. Health now follows command outcomes too:
    // connection-class failures from any Execute path mark the engine disconnected and clear the pools
    // so the next attempt really goes to the network, and ProbeConnectionAsync gives applications an
    // authoritative round trip to decide when the server is back.
    public partial class DBEngine
    {
        private volatile bool dbconnected = false;
        /// <summary>
        /// True while the engine believes the server is usable. Changes raise
        /// <see cref="DBConnectionStateChanged"/>. Set false by connection-class failures on open or on
        /// execute; set true by a successful open or <see cref="ProbeConnectionAsync"/>.
        /// </summary>
        public bool DBConnected
        {
            get => dbconnected;
            set
            {
                if (dbconnected == value) return;
                dbconnected = value;
                if (value)
                {
                    LastConnectionRestored = DateTime.Now;
                    Interlocked.Exchange(ref consecutiveConnectionFailures, 0);
                }
                RaiseConnectionStateChanged();
            }
        }

        /// <summary>
        /// Raised on whatever thread changed <see cref="DBConnected"/>. Handlers that touch UI must marshal
        /// to the UI thread themselves, and should not block.
        /// </summary>
        public event EventHandler DBConnectionStateChanged;

        public DateTime LastSuccessfulCommand { get; private set; } = DateTime.MinValue;
        public DateTime LastConnectionFailure { get; private set; } = DateTime.MinValue;
        public DateTime LastConnectionRestored { get; private set; } = DateTime.MinValue;
        private int consecutiveConnectionFailures;
        public int ConsecutiveConnectionFailures => consecutiveConnectionFailures;

        private void RaiseConnectionStateChanged()
        {
            var handler = DBConnectionStateChanged;
            if (handler == null) return;
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // A failing subscriber must not turn a state change into a data-access failure.
            }
        }

        // SQL Server / SNI error numbers that mean the connection or the path to the server is gone, as
        // opposed to a problem with the command. -2 (command timeout) is deliberately absent: a slow query
        // should not take a whole application offline. ProbeConnectionAsync treats its own timeout as a
        // failure, which is where a half-open connection is caught.
        private static readonly int[] ConnectionErrorNumbers =
        {
            53,     // server not found / not accessible
            64,     // specified network name is no longer available
            121,    // semaphore timeout (network dropped)
            233,    // no process on the other end of the pipe
            258,    // wait operation timed out (pre-login)
            1231,   // network location cannot be reached
            4060,   // cannot open database
            10053,  // connection aborted by the host
            10054,  // connection reset by peer
            10060,  // connection attempt timed out
            10061,  // connection refused
            10065,  // host unreachable
            11001,  // host not known
            17142,  // server paused
            18452,  // login from an untrusted domain (SSPI / Kerberos failure)
            40613,  // database unavailable
        };

        /// <summary>
        /// True when the exception indicates the connection or the server is unusable rather than that one
        /// command failed.
        /// </summary>
        public static bool IsConnectionFailure(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is SqlException sqlex)
                {
                    if (sqlex.Class >= 20) return true; // severity 20+ terminates the connection
                    foreach (SqlError err in sqlex.Errors)
                        if (Array.IndexOf(ConnectionErrorNumbers, err.Number) >= 0) return true;
                }
                else if (e is Win32Exception || e is System.Net.Sockets.SocketException || e is System.IO.IOException)
                {
                    return true;
                }
                else if (e is InvalidOperationException &&
                         e.Message.IndexOf("connection from the pool", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true; // pool exhausted - nothing will work until connections come back
                }
            }
            return false;
        }

        /// <summary>
        /// Feeds an exception from code that ran its own command on a connection from
        /// <see cref="GetConnection"/> / <see cref="GetConnectionAsync"/>, so those failures count toward
        /// health just as DBEngine's own Execute paths do. Returns true when it was a connection failure.
        /// </summary>
        public bool ReportException(Exception ex)
        {
            return RecordCommandFailure(ex);
        }

        private bool RecordCommandFailure(Exception ex)
        {
            if (!IsConnectionFailure(ex)) return false;
            MarkConnectionFailed(ex);
            return true;
        }

        private void RecordOpenFailure(Exception ex)
        {
            DBConnectionError = true;
            DBException = ex;
            LastConnectionFailure = DateTime.Now;
            Interlocked.Increment(ref consecutiveConnectionFailures);
            DBConnected = false;
            if (LogErrors)
            {
                Log.Entry(new DBExecutionEntry
                {
                    Source = "Connection",
                    Message = ex.Message,
                    Severity = 16,
                    Details = ex.ToString()
                }, 2);
            }
        }

        private void MarkConnectionFailed(Exception ex)
        {
            RecordOpenFailure(ex);
            // Pooled connections to a server we just lost are dead too. Leaving them in the pool means
            // the next Open() hands one back, succeeds without a round trip, and reports the server as up.
            // This clears every pool in the process, not just this engine's; that is the point - they
            // share the network path.
            try { SqlConnection.ClearAllPools(); } catch { }
        }

        /// <summary>
        /// Opens a fresh connection and runs <c>SELECT 1</c>, bounded by <paramref name="TimeoutSeconds"/>
        /// even if the driver itself hangs. Updates <see cref="DBConnected"/>, <see cref="DBConnectionError"/>
        /// and <see cref="DBException"/>, and clears the connection pools on failure.
        /// </summary>
        public async Task<bool> ProbeConnectionAsync(int TimeoutSeconds = 10, CancellationToken CancellationToken = default, string ApplicationName = null)
        {
            if (TimeoutSeconds < 1) TimeoutSeconds = 1;
            var probe = runprobeasync(TimeoutSeconds, CancellationToken, ApplicationName ?? $"{DefaultApplicationName}.Probe");
            // CommandTimeout and ConnectTimeout should end the probe on their own. A half-open TCP
            // connection is exactly the case where async SqlClient calls have been seen to wait forever,
            // so the probe gets an outer bound as well.
            var bound = Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds * 2 + 5), CancellationToken);
            var finished = await Task.WhenAny(probe, bound).ConfigureAwait(false);
            Exception failure;
            if (finished == probe)
            {
                failure = probe.Exception?.GetBaseException();
                if (failure == null && probe.IsCanceled) failure = new OperationCanceledException("Connection probe was cancelled.");
            }
            else
            {
                // Observe the abandoned probe so its eventual exception is not unobserved.
                _ = probe.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                failure = CancellationToken.IsCancellationRequested
                    ? new OperationCanceledException("Connection probe was cancelled.")
                    : (Exception)new TimeoutException($"Connection probe to {connectionstring.DataSource} did not complete within {TimeoutSeconds * 2 + 5} seconds.");
            }

            if (failure == null)
            {
                LastSuccessfulCommand = DateTime.Now;
                DBConnectionError = false;
                DBConnected = true;
                return true;
            }

            if (failure is OperationCanceledException && CancellationToken.IsCancellationRequested)
                return DBConnected; // cancelled by the caller - no verdict either way

            MarkConnectionFailed(failure);
            return false;
        }

        private async Task runprobeasync(int TimeoutSeconds, CancellationToken CancellationToken, string ApplicationName)
        {
            // createconnection rather than getconnectionasync: a successful Open() alone would set
            // DBConnected true before the round trip had proved anything.
            using (var cn = createconnection(TimeoutSeconds, ApplicationName))
            {
                if (cn == null) throw new InvalidOperationException("DBEngine has no connection string.");
                await cn.OpenAsync(CancellationToken).ConfigureAwait(false);
                using (var cmd = new SqlCommand("SELECT 1;", cn))
                {
                    cmd.CommandTimeout = TimeoutSeconds;
                    await cmd.ExecuteScalarAsync(CancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
