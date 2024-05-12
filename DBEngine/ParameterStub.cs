using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace MDDDataAccess
{
    public class ParameterStub
    {
        public ParameterStub(string name, object value)
        {
            Name = name;
            Value = value;
        }
        public ParameterStub(string name, object value, SqlDbType type) : this(name,value)
        {
            DBType = type;
        }
        public string Name { get; set; }
        public object Value { get; set; }
        public SqlDbType? DBType { get; set; }
    }
}
