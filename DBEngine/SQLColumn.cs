using System;
using System.Collections.Generic;
using System.Data;
using System.Security.Principal;
using System.Text;

namespace MDDDataAccess
{
    public class SQLColumn
    {
        [DBName("column_ordinal")]
        public int ordinal { get; set; }
       
        public string name { get;set; }
        public bool is_nullable { get; set; }
        private string _type;
        [DBName("system_type_name")]
        public string type
        {
            get => _type;
            set
            {
                //_origtype = value;
                if (value.Contains("("))
                    _type = value.Substring(0, value.IndexOf("(")).Trim();
                else
                    _type = value;
            }
        }
        //private string _origtype;
        //public string OrigType { get => _origtype; }
        public string full_type { get => DBEngine.GetFullSqlTypeName(type, max_length, precision, scale); }
        public short max_length { get; set; }
        public byte precision { get; set; }
        public byte scale { get; set; }
        public string collation_name { get; set; }
        [DBName("is_identity_column")]
        public bool is_identity { get; set; }
        public bool is_updateable { get; set; }
        [DBName("is_computed_column")]
        public bool is_computed { get; set; }
        public Type clr_type { get => DBEngine.GetClrType(type); }
        public override string ToString()
        {
            return $"{name} {full_type} {(is_nullable ? "NULL" : "NOT NULL")}";// ({_origtype})";
        }
        public static SQLColumn FromSchemaTableRow(DataRow row)
        {
            var type = (string)row["DataTypeName"];
            var maxLength = Convert.ToInt16(row["ColumnSize"]);

            // Adjust max_length for Unicode data types
            if (type.Equals("nvarchar", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("nchar", StringComparison.OrdinalIgnoreCase))
            {
                maxLength *= 2;
            }

            var column = new SQLColumn();
            column.ordinal = (int)row["ColumnOrdinal"];
            column.name = (string)row["ColumnName"];
            column.is_nullable = (bool)row["AllowDBNull"];
            column.type = type;
            column.max_length = maxLength;
            column.precision = Convert.ToByte(row["NumericPrecision"]);
            column.scale = Convert.ToByte(row["NumericScale"]);
            column.collation_name = null;
            column.is_identity = (bool)row["IsIdentity"];
            column.is_updateable = (bool)row["IsAutoIncrement"];
            column.is_computed = row["IsExpression"] == DBNull.Value ? false : (bool)row["IsExpression"];

            return column;
        }

    }
}
