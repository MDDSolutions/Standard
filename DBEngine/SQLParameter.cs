using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MDDDataAccess
{
    public class ProcedureParameter
    {
        public string name { get; set; }
        [DBOptional]
        public bool is_output { get; set; }
        public bool has_default_value { get; set; }
        [DBOptional]
        public bool is_identity { get; set; }



        [DBIgnore]
        public PropertyInfo ObjectProperty { get; set; }

        public string type_name { get; set; }
        public short max_length { get; set; }
        public byte precision { get; set; }
        public byte scale { get; set; }

        public string SQLDataTypeString()
        {
            if (type_name.Equals("decimal", StringComparison.OrdinalIgnoreCase) || type_name.Equals("numeric", StringComparison.OrdinalIgnoreCase))
                return $"{type_name}({precision}, {scale})";
            if (type_name.Equals("varchar", StringComparison.OrdinalIgnoreCase) 
                || type_name.Equals("char", StringComparison.OrdinalIgnoreCase)
                || type_name.Equals("nvarchar", StringComparison.OrdinalIgnoreCase)
                || type_name.Equals("nchar", StringComparison.OrdinalIgnoreCase)
                || type_name.Equals("varbinary", StringComparison.OrdinalIgnoreCase))
            {
                if (max_length == -1)
                    return $"{type_name}(max)";
                else
                    return $"{type_name}({max_length})";
            }
            return type_name;
        }


        public override string ToString()
        {
            return $"{name}";
        }

        public static ConcurrentDictionary<string, IList<ProcedureParameter>> ParamLists = new ConcurrentDictionary<string, IList<ProcedureParameter>>();

    }
}
