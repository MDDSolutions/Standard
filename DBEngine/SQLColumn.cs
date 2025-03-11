using System;
using System.Collections.Generic;
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

    }
}
