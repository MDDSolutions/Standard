using MDDDataAccess;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace DBEngineUnitTests
{
    public class TestTablePOCO
    {
        [ListKey]
        public Guid Id { get; set; }
        public DateTime ModifiedDate { get; set; }
        public byte TinyIntCol { get; set; }
        public short SmallIntCol { get; set; }
        public int IntCol { get; set; }
        public long BigIntCol { get; set; }
        public decimal DecimalCol { get; set; }
        public decimal MoneyCol { get; set; }
        public double FloatCol { get; set; }
        public float RealCol { get; set; }
        public string CharCol { get; set; }
        public string VarcharCol { get; set; }
        public string NvarcharCol { get; set; }
        public string TextCol { get; set; }
        public string NtextCol { get; set; }
        public Byte[] BinaryCol { get; set; }
        public Byte[] VarbinaryCol { get; set; }
        public DateTime DateCol { get; set; }
        public TimeSpan TimeCol { get; set; }
        public DateTime DateTimeCol { get; set; }
        public DateTimeOffset DateTimeOffsetCol { get; set; }
        public bool BitCol { get; set; }
        public Object SqlVariantCol { get; set; }
        //public XDocument XmlCol { get; set; }
    }
    public class TestTableNO : NotifierObject
    {
        [ListKey]
        public Guid Id { get => _id; set => SetProperty(ref _id, value); }
        public DateTime ModifiedDate { get => _modifieddate; set => SetProperty(ref _modifieddate, value); }
        public byte TinyIntCol { get => _tinyintcol; set => SetProperty(ref _tinyintcol, value); }
        public short SmallIntCol { get => _smallintcol; set => SetProperty(ref _smallintcol, value); }
        public int IntCol { get => _intcol; set => SetProperty(ref _intcol, value); }
        public long BigIntCol { get => _bigintcol; set => SetProperty(ref _bigintcol, value); }
        public decimal DecimalCol { get => _decimalcol; set => SetProperty(ref _decimalcol, value); }
        public decimal MoneyCol { get => _moneycol; set => SetProperty(ref _moneycol, value); }
        public double FloatCol { get => _floatcol; set => SetProperty(ref _floatcol, value); }
        public float RealCol { get => _realcol; set => SetProperty(ref _realcol, value); }
        public string CharCol { get => _charcol; set => SetProperty(ref _charcol, value); }
        public string VarcharCol { get => _varcharcol; set => SetProperty(ref _varcharcol, value); }
        public string NvarcharCol { get => _nvarcharcol; set => SetProperty(ref _nvarcharcol, value); }
        public string TextCol { get => _textcol; set => SetProperty(ref _textcol, value); }
        public string NtextCol { get => _ntextcol; set => SetProperty(ref _ntextcol, value); }
        public Byte[] BinaryCol { get => _binarycol; set => SetProperty(ref _binarycol, value); }
        public Byte[] VarbinaryCol { get => _varbinarycol; set => SetProperty(ref _varbinarycol, value); }
        public DateTime DateCol { get => _datecol; set => SetProperty(ref _datecol, value); }
        public TimeSpan TimeCol { get => _timecol; set => SetProperty(ref _timecol, value); }
        public DateTime DateTimeCol { get => _datetimecol; set => SetProperty(ref _datetimecol, value); }
        public DateTimeOffset DateTimeOffsetCol { get => _datetimeoffsetcol; set => SetProperty(ref _datetimeoffsetcol, value); }
        public bool BitCol { get => _bitcol; set => SetProperty(ref _bitcol, value); }
        public Object SqlVariantCol { get => _sqlvariantcol; set => SetProperty(ref _sqlvariantcol, value); }
        //public XDocument XmlCol { get => _xmlcol; set => SetProperty(ref _xmlcol, value); }

        //private XDocument _xmlcol;
        private Guid _id;
        private DateTime _modifieddate, _datecol, _datetimecol;
        private byte _tinyintcol;
        private short _smallintcol;
        private int _intcol;
        private long _bigintcol;
        private decimal _decimalcol, _moneycol;
        private double _floatcol;
        private float _realcol;
        private string _charcol, _varcharcol, _nvarcharcol, _textcol, _ntextcol;
        private Byte[] _binarycol, _varbinarycol;
        private TimeSpan _timecol;
        private DateTimeOffset _datetimeoffsetcol;
        private bool _bitcol;
        private Object _sqlvariantcol;
    }

}
