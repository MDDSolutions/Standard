using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;

namespace MDDDataAccess
{
    public partial class DBEngine
    {
        /// <summary>
        /// Opens a command that returns multiple result sets and hands back a <see cref="MultiResultReader"/>
        /// that owns the connection/command/reader for the duration. Read each result set in order with
        /// <see cref="MultiResultReader.ReadResultSet{T}()"/>; the reader advances between sets automatically.
        /// The returned object MUST be disposed (use a <c>using</c>) so the underlying connection closes.
        /// </summary>
        public MultiResultReader QueryMultiple(string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params SqlParameter[] list)
        {
            if (!IsProcedure && !AllowAdHoc) throw new Exception("Ad Hoc Queries are not allowed by this DBEngine");

            SqlConnection cn = null;
            SqlCommand cmd = null;
            try
            {
                cn = getconnection(ConnectionTimeout, ApplicationName);
                if (cn == null) return null;

                cmd = new SqlCommand(cmdtext, cn);
                cmd.CommandTimeout = CommandTimeout;
                if (IsProcedure) cmd.CommandType = CommandType.StoredProcedure;
                ParameterizeCommand(list, cmd);

                // Force a buffered, multi-result-set behavior. The instance default is SequentialAccess,
                // which forbids the random column access GetReaderValue relies on and forbids re-reading a
                // column the mapper already consumed. CloseConnection is non-zero so ExecuteReader will not
                // substitute the SequentialAccess default, and it lets reader disposal close the connection.
                var rdr = ExecuteReader(cmd, CommandBehavior.CloseConnection);
                return new MultiResultReader(this, cn, cmd, rdr);
            }
            catch
            {
                cmd?.Dispose();
                cn?.Dispose();
                throw;
            }
        }

        /// <summary>Stub-parameter overload of <see cref="QueryMultiple(string, bool, int, string, SqlParameter[])"/>.</summary>
        public MultiResultReader QueryMultipleStub(string cmdtext, bool IsProcedure, int ConnectionTimeout = -1, string ApplicationName = null, params ParameterStub[] list)
        {
            return QueryMultiple(cmdtext, IsProcedure, ConnectionTimeout, ApplicationName, StubsToSqlParameters(list));
        }
    }

    /// <summary>
    /// A forward-only walker over a command's multiple result sets. Created by
    /// <see cref="DBEngine.QueryMultiple(string, bool, int, string, SqlParameter[])"/>. Each call to
    /// <see cref="ReadResultSet{T}()"/> maps the current result set into objects and advances to the next set,
    /// so call them in the same order the result sets are returned. Owns the connection, command and reader;
    /// dispose it (via <c>using</c>) to release them.
    /// </summary>
    public sealed class MultiResultReader : IDisposable
    {
        private readonly DBEngine _engine;
        private readonly SqlConnection _connection;
        private readonly SqlCommand _command;
        private readonly SqlDataReader _reader;
        private bool _first = true;
        private bool _disposed;

        internal MultiResultReader(DBEngine engine, SqlConnection connection, SqlCommand command, SqlDataReader reader)
        {
            _engine = engine;
            _connection = connection;
            _command = command;
            _reader = reader;
        }

        /// <summary>Maps the current result set into a list and advances the reader to the next set.</summary>
        public IList<T> ReadResultSet<T>() where T : class, new()
        {
            return ReadResultSet<T>(null);
        }

        /// <summary>
        /// Maps the current result set into a list, invoking <paramref name="rowAction"/> per row before the
        /// row is added (use <see cref="GetReaderValue{T}"/> inside the action to read columns that are not
        /// mapped properties), then advances the reader to the next set.
        /// </summary>
        public IList<T> ReadResultSet<T>(Action<T> rowAction) where T : class, new()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MultiResultReader));

            // First set is the one the reader already sits on; every later call must advance first.
            if (!_first && !_reader.NextResult()) return new List<T>();
            _first = false;

            return _engine.ReadResultSet<T>(_reader, rowAction);
        }

        /// <summary>
        /// Reads a column from the row the reader is currently positioned on, coercing it to
        /// <typeparamref name="T"/> (DBNull becomes <c>default(T)</c>). Only valid while a row action passed to
        /// <see cref="ReadResultSet{T}(Action{T})"/> is executing — outside that scope the reader has already
        /// advanced past the row and the value is undefined.
        /// </summary>
        public T GetReaderValue<T>(string columnName)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MultiResultReader));

            var x = _reader[columnName];
            if (x == null || x == DBNull.Value) return default(T);

            var converter = TypeDescriptor.GetConverter(typeof(T));
            if (converter != null && converter.CanConvertFrom(x.GetType()))
                return (T)converter.ConvertFrom(x);
            return (T)Convert.ChangeType(x, typeof(T));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _reader?.Dispose();   // CloseConnection behavior closes the connection on reader disposal
            _command?.Dispose();
            _connection?.Dispose();
        }
    }
}
