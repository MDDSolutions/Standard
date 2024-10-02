using System;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace MDDDataAccess
{
    public class MSqlCommand : IDisposable
    {
        private readonly SqlCommand _sqlCommand;
        internal MSqlConnection _MSqlConnection;
        public virtual MSqlConnection Connection
        {
            get => _MSqlConnection;
            set
            {
                _MSqlConnection = value;
                _sqlCommand.Connection = value.GetInternalConnection();
            }
        }
        internal SqlCommand GetInternalCommand() => _sqlCommand;

        public MSqlCommand()
        {

        }
        public MSqlCommand(string commandText)
        {
            _sqlCommand = new SqlCommand(commandText);
        }
        public MSqlCommand(string commandText, string connectionString)
        {
            _MSqlConnection = new MSqlConnection(connectionString);
            _sqlCommand = new SqlCommand(commandText, _MSqlConnection.GetInternalConnection());
        }
        public MSqlCommand(string commandText, MSqlConnection MSqlConnection)
        {
            _sqlCommand = new SqlCommand(commandText, MSqlConnection.GetInternalConnection());
            _MSqlConnection = MSqlConnection;
        }

        public MSqlCommand(SqlCommand sqlCommand, MSqlConnection MSqlConnection)
        {
            _sqlCommand = sqlCommand;
            _sqlCommand.Connection = MSqlConnection.GetInternalConnection();
            _MSqlConnection = MSqlConnection;
        }

        public string CommandText
        {
            get => _sqlCommand.CommandText;
            set => _sqlCommand.CommandText = value;
        }

        public CommandType CommandType
        {
            get => _sqlCommand.CommandType;
            set => _sqlCommand.CommandType = value;
        }

        public int CommandTimeout
        {
            get => _sqlCommand.CommandTimeout;
            set => _sqlCommand.CommandTimeout = value;
        }

        public SqlParameterCollection Parameters => _sqlCommand.Parameters;

        public SqlTransaction Transaction
        {
            get => _sqlCommand.Transaction;
            set => _sqlCommand.Transaction = value;
        }

        public virtual UpdateRowSource UpdatedRowSource
        {
            get => _sqlCommand.UpdatedRowSource;
            set => _sqlCommand.UpdatedRowSource = value;
        }

        public void Cancel() => _sqlCommand.Cancel();

        public int ExecuteNonQuery() => _sqlCommand.ExecuteNonQuery();

        public async Task<int> ExecuteNonQueryAsync() => await _sqlCommand.ExecuteNonQueryAsync().ConfigureAwait(false);

        public async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => await _sqlCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        public SqlDataReader ExecuteReader() => _sqlCommand.ExecuteReader();

        public SqlDataReader ExecuteReader(CommandBehavior behavior) => _sqlCommand.ExecuteReader(behavior);

        public async Task<SqlDataReader> ExecuteReaderAsync() => await _sqlCommand.ExecuteReaderAsync().ConfigureAwait(false);

        public async Task<SqlDataReader> ExecuteReaderAsync(CommandBehavior behavior) => await _sqlCommand.ExecuteReaderAsync(behavior).ConfigureAwait(false);

        public async Task<SqlDataReader> ExecuteReaderAsync(CancellationToken cancellationToken) => await _sqlCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        public async Task<SqlDataReader> ExecuteReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) => await _sqlCommand.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);

        public object ExecuteScalar() => _sqlCommand.ExecuteScalar();

        public async Task<object> ExecuteScalarAsync() => await _sqlCommand.ExecuteScalarAsync().ConfigureAwait(false);

        public async Task<object> ExecuteScalarAsync(CancellationToken cancellationToken) => await _sqlCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        public void Prepare() => _sqlCommand.Prepare();
        public void Dispose() => _sqlCommand?.Dispose();
    }
}
