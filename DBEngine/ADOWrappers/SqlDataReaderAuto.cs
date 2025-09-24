using System;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace MDDDataAccess
{
    public class SqlDataReaderAuto : IDisposable
    {
        private readonly SqlConnection _connection;
        private readonly SqlCommand _command;
        private SqlDataReader _reader;
        private bool _disposed = false;

        private SqlDataReaderAuto(bool isAsync, string connectionString, string commandText, CommandType commandType, params SqlParameter[] parameters)
        {
            _connection = new SqlConnection(connectionString);
            _command = new SqlCommand(commandText, _connection)
            {
                CommandType = commandType
            };

            if (parameters != null)
            {
                _command.Parameters.AddRange(parameters);
            }

            if (!isAsync)
            {
                OpenConnection();
                _reader = _command.ExecuteReader();
            }
        }

        public SqlDataReaderAuto(string connectionString, string commandText, CommandType commandType, params SqlParameter[] parameters)
            : this(false, connectionString, commandText, commandType, parameters)
        {
        }

        public static async Task<SqlDataReaderAuto> CreateAsync(string connectionString, string commandText, CommandType commandType, params SqlParameter[] parameters)
        {
            var instance = new SqlDataReaderAuto(true, connectionString, commandText, commandType, parameters);
            await instance.OpenConnectionAsync().ConfigureAwait(false);
            instance._reader = await instance._command.ExecuteReaderAsync().ConfigureAwait(false);
            return instance;
        }

        private void OpenConnection()
        {
            try
            {
                _connection.Open();
            }
            catch (Exception ex)
            {
                // Handle or log the exception as needed
                throw new InvalidOperationException("Failed to open the SQL connection.", ex);
            }
        }

        private async Task OpenConnectionAsync()
        {
            try
            {
                await _connection.OpenAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Handle or log the exception as needed
                throw new InvalidOperationException("Failed to open the SQL connection.", ex);
            }
        }

        public object this[int index] => _reader[index];
        public object this[string name] => _reader[name];

        public bool Read() => _reader.Read();

        //public async Task<bool> ReadAsync() => await _reader.ReadAsync().ConfigureAwait(false);

        public string GetString(int index) => _reader.GetString(index);
        public int GetInt32(int index) => _reader.GetInt32(index);
        public int FieldCount => _reader.FieldCount;
        public bool HasRows => _reader.HasRows;
        public bool IsClosed => _reader.IsClosed;
        public int RecordsAffected => _reader.RecordsAffected;

        public void Close() => _reader.Close();
        public bool GetBoolean(int ordinal) => _reader.GetBoolean(ordinal);
        public byte GetByte(int ordinal) => _reader.GetByte(ordinal);
        public long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length) => _reader.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public char GetChar(int ordinal) => _reader.GetChar(ordinal);
        public long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length) => _reader.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public string GetDataTypeName(int ordinal) => _reader.GetDataTypeName(ordinal);
        public DateTime GetDateTime(int ordinal) => _reader.GetDateTime(ordinal);
        public decimal GetDecimal(int ordinal) => _reader.GetDecimal(ordinal);
        public double GetDouble(int ordinal) => _reader.GetDouble(ordinal);
        public Type GetFieldType(int ordinal) => _reader.GetFieldType(ordinal);
        public Guid GetGuid(int ordinal) => _reader.GetGuid(ordinal);
        public short GetInt16(int ordinal) => _reader.GetInt16(ordinal);
        public long GetInt64(int ordinal) => _reader.GetInt64(ordinal);
        public string GetName(int ordinal) => _reader.GetName(ordinal);
        public int GetOrdinal(string name) => _reader.GetOrdinal(name);
        public DataTable GetSchemaTable() => _reader.GetSchemaTable();
        public object GetValue(int ordinal) => _reader.GetValue(ordinal);
        public int GetValues(object[] values) => _reader.GetValues(values);
        public bool IsDBNull(int ordinal) => _reader.IsDBNull(ordinal);
        public bool NextResult() => _reader.NextResult();
        public async Task<bool> NextResultAsync() => await _reader.NextResultAsync().ConfigureAwait(false);
        private static int _disposeCount = 0; // Static counter to track Dispose calls
        public static int DisposeCount => _disposeCount; // Static property to return the Dispose count
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Ensure the reader is closed before disposing of the command and connection
                    _reader?.Close();
                    _reader?.Dispose();
                    _command?.Dispose();
                    _connection?.Dispose();
                    Interlocked.Increment(ref _disposeCount);
                }

                _disposed = true;
            }
        }
        ~SqlDataReaderAuto()
        {
            Dispose(false);
        }
    }
}
