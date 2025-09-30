using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MDDDataAccess
{
    public class TableViewModel : INotifyPropertyChanged
    {
        #region Initialization
        private DataView dataView;
        private SqlDataAdapter dataAdapter;
        private void Initialize()
        {
            if (!UpdateCurrentRow(0)) throw new Exception("Table is empty");
            NextCommand = new RelayCommand(Next, CanMoveNext);
            PreviousCommand = new RelayCommand(Previous, CanMovePrevious);
            SaveCommand = new RelayCommand(Save, CanSave);
            InsertCommand = new RelayCommand(Insert, CanInsert);
            DeleteCommand = new RelayCommand(Delete, CanDelete);
            FirstCommand = new RelayCommand(First, CanMoveFirst);
            LastCommand = new RelayCommand(Last, CanMoveLast);
        }
        public TableViewModel(DataTable dataTable)
        {
            dataView = dataTable.DefaultView;
            Initialize();
        }
        public TableViewModel(SqlDataAdapter adapter)
        {
            dataAdapter = adapter;
            DataTable dataTable = new DataTable();
            dataAdapter.Fill(dataTable);
            dataView = dataTable.DefaultView;

            Initialize();
        }
        public TableViewModel(TableDefinition table, string connectionstring, string selectstatement = null)
        {
            SqlDataAdapter adapter;
            if (selectstatement != null)
                adapter = new SqlDataAdapter(selectstatement, connectionstring);
            else
            {
                var cn = new SqlConnection(connectionstring);
                var cmd = table.GetSelectCommand();
                cmd.Connection = cn;
                adapter = new SqlDataAdapter(cmd);
            }

            tabledefinition = table;


            //var cmdbuilder = new SqlCommandBuilder(adapter);
            //var cmd = cmdbuilder.GetUpdateCommand(true);

            adapter.UpdateCommand = table.GetUpdateCommand();
            adapter.UpdateCommand.UpdatedRowSource = UpdateRowSource.FirstReturnedRecord;
            adapter.UpdateCommand.Connection = adapter.SelectCommand.Connection;

            adapter.InsertCommand = table.GetInsertCommand();
            adapter.InsertCommand.UpdatedRowSource = UpdateRowSource.FirstReturnedRecord;
            adapter.InsertCommand.Connection = adapter.SelectCommand.Connection;

            adapter.DeleteCommand = table.GetDeleteCommand();
            adapter.DeleteCommand.Connection = adapter.SelectCommand.Connection;

            dataAdapter = adapter;
            DataTable dataTable = new DataTable();
            dataAdapter.Fill(dataTable);
            dataView = dataTable.DefaultView;

            Initialize();
        }

        #endregion

        #region events and properties
        public bool Updatable => dataAdapter != null &&
                         dataAdapter.InsertCommand != null &&
                         dataAdapter.UpdateCommand != null &&
                         dataAdapter.DeleteCommand != null;
        private int currentIndex;
        private DataRowView currentRow;
        public DataRowView CurrentRow => currentRow;
        public string Sort
        {
            get => dataView.Sort;
            set
            {
                if (UpdateCurrentRow(0))
                {
                    dataView.Sort = value;
                    OnPropertyChanged();
                    UpdateCurrentRow(0);
                }
            }
        }
        private string filter;
        public string Filter
        {
            get => filter;
            set
            {
                filter = value;
                UpdateFilter();
            }
        }
        DateTime lastFilterUpdate = DateTime.MaxValue;
        private async void UpdateFilter()
        {
            lastFilterUpdate = DateTime.Now;
            await Task.Delay(600);
            var now = DateTime.Now;
            if (lastFilterUpdate.AddMilliseconds(595) < now && UpdateCurrentRow(0))
            {
                var expr = string.Empty;
                if (!string.IsNullOrWhiteSpace(filter))
                foreach (var item in tabledefinition.Columns)
                {
                    var clrType = DBEngine.GetClrType(item.SqlDbType);
                    if (clrType == typeof(string))
                    {
                        if (expr != string.Empty) expr += " OR ";
                        expr += $"[{item.Name}] LIKE '%{filter}%'";
                    }
                    else if (clrType == typeof(DateTime) || clrType == typeof(int) || clrType == typeof(decimal) || clrType == typeof(float) || clrType == typeof(double) || clrType == typeof(long) || clrType == typeof(byte) || clrType == typeof(short))
                    {
                        if (expr != string.Empty) expr += " OR ";
                        expr += $"CONVERT([{item.Name}], 'System.String') LIKE '%{filter}%'";
                    }
                }
                dataView.RowFilter = expr;
                OnPropertyChanged("Filter");
                UpdateCurrentRow(-1);
            }
        }
        private TableDefinition tabledefinition;
        public IEnumerable<ColumnDefinition> ColumnList 
        {
            get
            {
                if (tabledefinition == null && tabledefinition.Columns == null)
                {
                    return dataView.Table.Columns
                        .Cast<DataColumn>()
                        .Select(column => new ColumnDefinition { Name = column.ColumnName })
                        .ToList();
                }
                return tabledefinition.Columns;
            }
        }
        public Func<bool?> SaveChanges { get; set; } = () => true;
        public int CurrentRowIndex 
        {   get => currentIndex + 1;
            set
            {
                if (value > 0 && value <= dataView.Count)
                {
                    UpdateCurrentRow(value - 1);
                }
            }
        }
        public int RowCount => dataView.Count;
        public ICommand NextCommand { get; private set; }
        public ICommand PreviousCommand { get; private set; }
        public ICommand SaveCommand { get; private set; }
        public ICommand InsertCommand { get; private set; }
        public ICommand DeleteCommand { get; private set; }
        public ICommand FirstCommand { get; private set; }
        public ICommand LastCommand { get; private set; }
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            Console.WriteLine($"OnPropertyChanged: {propertyName}");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion


        private CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

        public async Task DirtyCheck()
        {
            // Cancel the previous task if it is still running
            cancellationTokenSource.Cancel();
            cancellationTokenSource = new CancellationTokenSource();

            try
            {
                await Task.Delay(50, cancellationTokenSource.Token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
            (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private bool UpdateCurrentRow(int newindex)
        {
            var oldindex = currentIndex;
            if (Updatable)
            {
                if (CanSave())
                {
                    bool? result = SaveChanges();
                    if (result == true)
                    {
                        Save();
                    }
                    else if (result == false)
                    {
                        if (newindex == -1) newindex = currentIndex;
                        dataView.Table.RejectChanges();
                    }
                    else
                    {
                        if (newindex == -1)
                        {
                            dataView.Table.RejectChanges();
                        }
                        return false;
                    }
                }
            }
            if (newindex == -1)
            {
                newindex = currentIndex;
                oldindex = -1;
            }

            if (dataView.Count > 0)
            {
                currentIndex = Math.Min(newindex, dataView.Count - 1);
                if (currentIndex < 0) currentIndex = 0;
                currentRow = dataView[currentIndex];
            }
            else
            {
                currentIndex = -1;
                currentRow = null;
            }
            if (oldindex != currentIndex || currentIndex == -1)
            {
                OnPropertyChanged("CurrentRow");
                OnPropertyChanged("RowCount");
                OnPropertyChanged("CurrentRowIndex");
                (NextCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (PreviousCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (FirstCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (LastCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
            return true;
        }
        private void Next() { if (currentIndex < dataView.Count - 1) UpdateCurrentRow(currentIndex + 1); }
        private bool CanMoveNext() => currentIndex < dataView.Count - 1;
        private void Previous() { if (currentIndex > 0) UpdateCurrentRow(currentIndex - 1); }
        private bool CanMovePrevious() => currentIndex > 0;
        private void First() { UpdateCurrentRow(0); }
        private bool CanMoveFirst() => currentIndex > 0;
        private void Last() { UpdateCurrentRow(dataView.Count - 1); }
        private bool CanMoveLast() => currentIndex < dataView.Count - 1;
        private void Save()
        {
            currentRow?.BeginEdit();
            currentRow?.EndEdit();
            try
            {
                dataAdapter.Update(dataView.Table);
            }
            catch (DBConcurrencyException ex)
            {
                Console.WriteLine($"Concurrency error: {ex.Message}");
                using (var tmpadapter = new SqlDataAdapter(tabledefinition.GetSelectCommandByID()))
                {
                    tmpadapter.SelectCommand.Connection = dataAdapter.SelectCommand.Connection;
                    foreach (SqlParameter p in tmpadapter.SelectCommand.Parameters)
                    {
                        p.Value = ex.Row[p.SourceColumn, DataRowVersion.Original];
                    }
                    DataTable tmpTable = new DataTable();
                    tmpadapter.Fill(tmpTable);
                    if (tmpTable.Rows.Count == 1)
                    {
                        ex.Row.ItemArray = tmpTable.Rows[0].ItemArray;
                        ex.Row.AcceptChanges();
                        throw new Exception("Concurrency Error - Row has been reloaded - changes have been discarded");
                    }
                    else
                    {
                        //dataView.Table.Rows.Remove(ex.Row);
                        //dataView.Delete(currentIndex);
                        if (ex.Row.RowState != DataRowState.Deleted)
                        {
                            dataView.Table.Rows.Remove(ex.Row);
                        }
                        dataView.Table.AcceptChanges();
                        currentRow = null;
                        UpdateCurrentRow(-1);
                        throw new Exception("Concurrency Error - Row has been deleted");
                    }
                }
            }
            (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        private bool CanSave()
        {
            if (dataAdapter == null) return false;
            if (dataAdapter.InsertCommand == null || dataAdapter.UpdateCommand == null || dataAdapter.DeleteCommand == null) return false;
            if (currentRow == null) return false;
            if (currentRow.Row.HasVersion(DataRowVersion.Proposed))
            {
                foreach (DataColumn column in dataView.Table.Columns)
                {
                    if (!Equals(currentRow.Row[column.ColumnName, DataRowVersion.Proposed], currentRow.Row[column.ColumnName, DataRowVersion.Current]))
                    {
                        return true;
                    }
                }
            }
            if (!currentRow.Row.HasVersion(DataRowVersion.Original)) return true;
            if (!currentRow.Row.HasVersion(DataRowVersion.Current)) return true;
            foreach (DataColumn column in dataView.Table.Columns)
            {
                if (!Equals(currentRow.Row[column.ColumnName, DataRowVersion.Original], currentRow.Row[column.ColumnName, DataRowVersion.Current]))
                {
                    return true;
                }
            }
            return false;
        }
        private void Insert()
        {
            DataRow newRow = dataView.Table.NewRow();
            dataView.Table.Rows.Add(newRow);
            UpdateCurrentRow(dataView.Count - 1);
        }
        private bool CanInsert() => Updatable;
        private void Delete()
        {
            if (currentRow != null)
            {
                currentRow.Delete();
                //if (currentIndex > 0)
                UpdateCurrentRow(-1);
                //else
                //    UpdateCurrentRow(1);
            }
        }
        private bool CanDelete() => Updatable && currentRow != null;
        public override string ToString() => $"CurrentRow: {CurrentRowIndex} - {currentRow?.Row.RowState}";
    }
    public enum ViewModelQueryType
    {
        All, FullText, Indexed
    }
}
