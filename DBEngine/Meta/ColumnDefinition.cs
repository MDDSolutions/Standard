using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace MDDDataAccess
{
    public class ColumnDefinition
    {
        public string Name { get; set; }
        public string CodeFriendlyName => $"{Name.Replace(" ","_")}";
        public int TableObjectID { get; set; }
        public int ColumnID { get; set; }
        public string DataType { get; set; }
        public short MaxLength { get; set; }
        public byte Precision { get; set; }
        public byte Scale { get; set; }
        public bool IsNullable { get; set; }
        public bool IsIdentity { get; set; }
        public bool IsComputed { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool HasDefault { get; set; }
        //public bool IsForeignKey { get; set; }
        public TableDefinition Table { get; set; }
        [DBIgnore]
        public SqlDbType SqlDbType { get; set; }
        [DBIgnore]
        public bool IsSystem { get; set; }
        [DBIgnore]
        public bool IsHiddenForDisplay { get; set; }
        [DBIgnore]
        public bool IsReadOnly { get; set; }

        public override string ToString() => $"{Name} {DBEngine.GetFullSqlTypeName(DataType.ToString(), MaxLength, Precision, Scale)}";
    }
    public class IndexColumnDefinition
    {
        public ColumnDefinition Column { get; set; }
        public bool IsIncluded { get; set; }
        public int IndexColumnID { get; set; }
        public bool FirstOrderColumn { get; set; }
        public override string ToString()
        {
            if (Column == null) return "Column is null";
            return $"{Column.Name} FirstOrder: {FirstOrderColumn} Included: {IsIncluded}";
        }
    }
}
