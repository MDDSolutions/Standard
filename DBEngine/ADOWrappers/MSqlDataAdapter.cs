using System;
using System.Data.SqlClient;
using System.Data;

namespace MDDDataAccess
{
    public class MSqlDataAdapter : IDisposable
    {
        private SqlDataAdapter _sqlDataAdapter;
        public MSqlDataAdapter()
        {
            _sqlDataAdapter = new SqlDataAdapter();
        }

        public MSqlDataAdapter(SqlCommand selectCommand)
        {
            _sqlDataAdapter = new SqlDataAdapter(selectCommand);
        }
        public MSqlDataAdapter(MSqlCommand selectCommand)
        {
            _sqlDataAdapter = new SqlDataAdapter(selectCommand.GetInternalCommand());
        }

        public MSqlDataAdapter(SqlCommand selectCommand, MSqlConnection conn)
        {
            _sqlDataAdapter = new SqlDataAdapter(selectCommand);
            selectCommand.Connection = conn.GetInternalConnection();
        }

        public MSqlDataAdapter(string selectCommandText, MSqlConnection conn)
        {
            _sqlDataAdapter = new SqlDataAdapter(selectCommandText, conn.GetInternalConnection());
        }

        public MSqlDataAdapter(string selectCommandText, SqlConnection selectConnection)
        {
            _sqlDataAdapter = new SqlDataAdapter(selectCommandText, selectConnection);
        }

        public MSqlDataAdapter(string selectCommandText, string selectConnectionString)
        {
            _sqlDataAdapter = new SqlDataAdapter(selectCommandText, selectConnectionString);
        }

        public MSqlCommand SelectCommand
        {
            get => new MSqlCommand(_sqlDataAdapter.SelectCommand, new MSqlConnection(_sqlDataAdapter.SelectCommand.Connection.ConnectionString));
            set => _sqlDataAdapter.SelectCommand = value.GetInternalCommand();
        }

        public MSqlCommand InsertCommand
        {
            get => new MSqlCommand(_sqlDataAdapter.InsertCommand, new MSqlConnection(_sqlDataAdapter.InsertCommand.Connection.ConnectionString));
            set => _sqlDataAdapter.InsertCommand = value.GetInternalCommand();
        }

        public MSqlCommand UpdateCommand
        {
            get => new MSqlCommand(_sqlDataAdapter.UpdateCommand, new MSqlConnection(_sqlDataAdapter.UpdateCommand.Connection.ConnectionString));
            set => _sqlDataAdapter.UpdateCommand = value.GetInternalCommand();
        }

        public MSqlCommand DeleteCommand
        {
            get => new MSqlCommand(_sqlDataAdapter.DeleteCommand, new MSqlConnection(_sqlDataAdapter.DeleteCommand.Connection.ConnectionString));
            set => _sqlDataAdapter.DeleteCommand = value.GetInternalCommand();
        }

        public MissingMappingAction MissingMappingAction
        {
            get => _sqlDataAdapter.MissingMappingAction;
            set => _sqlDataAdapter.MissingMappingAction = value;
        }

        public MissingSchemaAction MissingSchemaAction
        {
            get => _sqlDataAdapter.MissingSchemaAction;
            set => _sqlDataAdapter.MissingSchemaAction = value;
        }

        public ITableMappingCollection TableMappings => _sqlDataAdapter.TableMappings;

        public int Fill(DataSet dataSet)
        {
            OpenConnectionIfNeeded(SelectCommand);
            return _sqlDataAdapter.Fill(dataSet);
        }

        public int Fill(DataTable dataTable)
        {
            OpenConnectionIfNeeded(SelectCommand);
            return _sqlDataAdapter.Fill(dataTable);
        }

        public DataTable[] FillSchema(DataSet dataSet, SchemaType schemaType)
        {
            OpenConnectionIfNeeded(SelectCommand);
            return _sqlDataAdapter.FillSchema(dataSet, schemaType);
        }

        public IDataParameter[] GetFillParameters() => _sqlDataAdapter.GetFillParameters();

        public int Update(DataSet dataSet)
        {
            OpenConnectionIfNeeded(InsertCommand);
            OpenConnectionIfNeeded(UpdateCommand);
            OpenConnectionIfNeeded(DeleteCommand);
            return _sqlDataAdapter.Update(dataSet);
        }

        public bool ShouldSerializeAcceptChangesDuringFill() => _sqlDataAdapter.ShouldSerializeAcceptChangesDuringFill();

        public bool ShouldSerializeFillLoadOption() => _sqlDataAdapter.ShouldSerializeFillLoadOption();

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _sqlDataAdapter?.Dispose();
                _sqlDataAdapter = null;
            }
        }

        private void OpenConnectionIfNeeded(MSqlCommand command)
        {
            var cn = command?.Connection;
            if (cn != null)
            {
                if (cn.State == ConnectionState.Closed)
                {
                    cn.Open();
#if DEBUG
                    MSqlConnection.ConnectionStats.AddOrUpdate(cn.ConnectionString, 1, (key, oldValue) => oldValue + 1);
#endif
                }
            }
        }
    }
}
