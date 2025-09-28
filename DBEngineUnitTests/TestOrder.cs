using MDDDataAccess;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace DBEngineUnitTests
{
    public class TestOrderPOCO
    {
        [ListKey]
        [DBName("OrderId")]
        public int Id { get; set; }

        [ListConcurrency]
        public byte[] RowVersion { get; set; }

        public string CustomerName { get; set; }
        public decimal Amount { get; set; }
    }
    public class TestOrderBadProperty
    {
        [ListKey]
        [DBName("OrderId")]
        public int Id { get; set; }

        [ListConcurrency]
        public byte[] RowVersion { get; set; }

        public int CustomerName { get; set; } //wrong data type - should be a string
        public decimal Amount { get; set; }
    }
    public class TestOrderWithAttributes
    {
        [ListKey]
        [DBName("OrderId")]
        public int Id { get; set; }

        [ListConcurrency]
        [DBName("RowVersion")]
        public byte[] RowVersion { get; set; }
        [DBName("CustomerName")]
        public string Name { get; set; }
        public decimal Amount { get; set; }
        [DBOptional]
        public int DBOptionalProperty { get; set; }
        [DBIgnore]
        public int DBIgnoreProperty { get; set; }
        [DBLoadedTime]
        public DateTime DBLoaded { get; set; }
    }
    public class TestOrderNO : NotifierObject
    {
        [ListKey]
        [DBName("OrderId")]
        public int Id { get => id; set => SetProperty(ref id, value); }

        [ListConcurrency]
        public byte[] RowVersion { get => rowversion; set => SetProperty(ref rowversion, value); }

        public string CustomerName { get => customername; set => SetProperty(ref customername, value); }
        public decimal Amount { get => amount; set => SetProperty(ref amount, value); }

        private int id;
        private byte[] rowversion;
        private string customername;
        private decimal amount;
    }
    public class TestOrderINPC : INotifyPropertyChanged
    {
        private int _id;
        [ListKey]
        [DBName("OrderId")]
        public int Id
        {
            get => _id;
            set
            {
                if (_id != value)
                {
                    _id = value;
                    OnPropertyChanged();
                }
            }
        }

        private byte[] _rowVersion;
        [ListConcurrency]
        [DBName("RowVersion")]
        public byte[] RowVersion
        {
            get => _rowVersion;
            set
            {
                if (_rowVersion != value)
                {
                    _rowVersion = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _customerName;
        public string CustomerName
        {
            get => _customerName;
            set
            {
                if (_customerName != value)
                {
                    _customerName = value;
                    OnPropertyChanged();
                }
            }
        }

        private decimal _amount;
        public decimal Amount
        {
            get => _amount;
            set
            {
                if (_amount != value)
                {
                    _amount = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
