using System;
using System.Collections.Concurrent;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using System.Threading;

namespace MDDDataAccess
{
    public class MSqlConnection : IDisposable
    {
        private readonly SqlConnection _sqlConnection;
        private bool _disposed = false;
        internal SqlConnection GetInternalConnection() => _sqlConnection;

        public MSqlConnection(string connectionString)
        {
            _sqlConnection = new SqlConnection(connectionString);
        }
        public void Open()
        {
            try
            {
                _sqlConnection.Open();
#if DEBUG
                ConnectionStats.AddOrUpdate(_sqlConnection.ConnectionString, 1, (key, value) => value + 1);
#endif
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to open the SQL connection.", ex);
            }
        }
        public async Task OpenAsync()
        {
            try
            {
                await _sqlConnection.OpenAsync().ConfigureAwait(false);
#if DEBUG
                ConnectionStats.AddOrUpdate(_sqlConnection.ConnectionString, 1, (key, value) => value + 1);
#endif
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to open the SQL connection.", ex);
            }
        }

        public async Task OpenAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
#if DEBUG
                ConnectionStats.AddOrUpdate(_sqlConnection.ConnectionString, 1, (key, value) => value + 1);
#endif
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to open the SQL connection.", ex);
            }
        }
        public ConnectionState State => _sqlConnection.State;
        public void Close() => _sqlConnection.Close();
        public string ConnectionString => _sqlConnection.ConnectionString;
        public int ConnectionTimeout => _sqlConnection.ConnectionTimeout;
        public string Database => _sqlConnection.Database;
        public string DataSource => _sqlConnection.DataSource;
        public string ServerVersion => _sqlConnection.ServerVersion;
        public string WorkstationId => _sqlConnection.WorkstationId;
        public int PacketSize => _sqlConnection.PacketSize;
        public Guid ClientConnectionId => _sqlConnection.ClientConnectionId;

        public void ChangeDatabase(string database) => _sqlConnection.ChangeDatabase(database);

        public SqlTransaction BeginTransaction() => _sqlConnection.BeginTransaction();

        public SqlTransaction BeginTransaction(IsolationLevel isolationLevel) => _sqlConnection.BeginTransaction(isolationLevel);

        public MSqlCommand CreateCommand() => new MSqlCommand(_sqlConnection.CreateCommand(), this);

        public event StateChangeEventHandler StateChange
        {
            add { _sqlConnection.StateChange += value; }
            remove { _sqlConnection.StateChange -= value; }
        }

        public event SqlInfoMessageEventHandler InfoMessage
        {
            add { _sqlConnection.InfoMessage += value; }
            remove { _sqlConnection.InfoMessage -= value; }
        }
        public object Clone() => new MSqlConnection(_sqlConnection.ConnectionString);
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources
                    if (_sqlConnection != null)
                    {
                        if (_sqlConnection.State == ConnectionState.Open)
                            _sqlConnection.Close();

                        _sqlConnection.Dispose();
                    }
                }
                // Dispose unmanaged resources (if any)

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Failsafe dispose
        /// </summary>
        ~MSqlConnection()
        {
            Dispose(false);
        }

        public static ConcurrentDictionary<string, int> ConnectionStats = new ConcurrentDictionary<string, int>();
    }
}
