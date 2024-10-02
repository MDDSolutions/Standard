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
        private void Initialize(DataTable dataTable)
        {
            dataView = dataTable.DefaultView;
            if (!UpdateCurrentRow(0)) throw new Exception("Table is empty");
            NextCommand = new RelayCommand(Next, CanMoveNext);
            PreviousCommand = new RelayCommand(Previous, CanMovePrevious);
            SaveCommand = new RelayCommand(Save, CanSave);
            InsertCommand = new RelayCommand(Insert, CanInsert);
            DeleteCommand = new RelayCommand(Delete, CanDelete);
        }
        public TableViewModel(DataTable dataTable) => Initialize(dataTable);
        public TableViewModel(SqlDataAdapter adapter)
        {
            dataAdapter = adapter;
            DataTable dataTable = new DataTable();
            dataAdapter.Fill(dataTable);

            Initialize(dataTable);
        }
        public TableViewModel(SqlDataAdapter adapter, IEnumerable<IColumnDefinition> columns) : this(adapter)
        {
            ColumnList = columns;
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
        public string Filter
        {
            get => dataView.RowFilter;
            set
            {
                if (UpdateCurrentRow(0))
                {
                    dataView.RowFilter = value;
                    OnPropertyChanged();
                    UpdateCurrentRow(0);
                }
            }
        }
        private IEnumerable<IColumnDefinition> columnList;
        public IEnumerable<IColumnDefinition> ColumnList 
        {
            get
            {
                if (columnList == null)
                {
                    columnList = dataView.Table.Columns
                        .Cast<DataColumn>()
                        .Select(column => new ColumnDefinition { Name = column.ColumnName })
                        .ToList();
                }
                return columnList;
            }
            set => columnList = value;
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
                        if (newindex == -1)
                        {
                            newindex = currentIndex > 0 ? currentIndex - 1 : 0;
                            oldindex = -1;
                        }
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
            if (dataView.Count > 0)
            {
                currentIndex = Math.Min(newindex, dataView.Count - 1);
                currentRow = dataView[currentIndex];
            }
            else
            {
                currentIndex = -1;
                currentRow = null;
            }
            if (oldindex != currentIndex)
            {
                OnPropertyChanged("CurrentRow");
                OnPropertyChanged("RowCount");
                OnPropertyChanged("CurrentRowIndex");
                (NextCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (PreviousCommand as RelayCommand)?.RaiseCanExecuteChanged();       
                //(InsertCommand as RelayCommand)?.RaiseCanExecuteChanged();
                //(DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
            return true;
        }
        private void Next() { if (currentIndex < dataView.Count - 1) UpdateCurrentRow(currentIndex + 1); }
        private bool CanMoveNext() => currentIndex < dataView.Count - 1;
        private void Previous() { if (currentIndex > 0) UpdateCurrentRow(currentIndex - 1); }
        private bool CanMovePrevious() => currentIndex > 0;
        private void Save()
        {
            currentRow?.BeginEdit();
            currentRow?.EndEdit();
            dataAdapter.Update(dataView.Table);
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

    public class RelayCommand : ICommand
    {
        private readonly Action execute;
        private readonly Func<bool> canExecute;
        private event EventHandler canExecuteChanged;
        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
            this.canExecute = canExecute;
        }
        public bool CanExecute(object parameter) => canExecute == null || canExecute();
        public void Execute(object parameter) => execute();
        public event EventHandler CanExecuteChanged
        {
            add
            {
                if (canExecute != null)
                {
                    canExecuteChanged += value;
                }
            }
            remove
            {
                if (canExecute != null)
                {
                    canExecuteChanged -= value;
                }
            }
        }
        public void RaiseCanExecuteChanged()
        {
            canExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
