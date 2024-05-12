using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace MDDDataAccess
{
    public partial class DBEngine
    {
        public IList<SQLColumn> DescribeResultset(string tsql)
        {
            return SqlRunQueryWithResults<SQLColumn>("sys.sp_describe_first_result_set", true, -1, null, GetParameter(() => tsql));
        }
        public IList<SQLColumn> ObjectColumns(string objectname)
        {
            var @query = @"SELECT	c.column_id AS column_ordinal,
		                            c.name,
		                            c.is_nullable,
		                            TYPE_NAME(c.system_type_id) AS system_type_name,
		                            c.max_length,
		                            c.precision,
		                            c.scale,
		                            c.collation_name,
		                            c.is_identity AS is_identity_column,
		                            CAST(CASE WHEN c.is_computed = 0 THEN 1 ELSE 0 END AS BIT) AS is_updateable,
		                            c.is_computed as is_computed_column
                            FROM sys.columns c
                            WHERE c.object_id = OBJECT_ID(@objectname);";
            var allowadhoc = AllowAdHoc;
            AllowAdHoc = true;
            var r = SqlRunQueryWithResults<SQLColumn>(query,false,-1,null, GetParameter(() => objectname));
            AllowAdHoc = allowadhoc;
            return r;
        }
        private static SqlDbType GetSqlType(Type type)
        {
            SqlDbType val;

            if (type == typeof(String))
            {
                val = SqlDbType.VarChar;
            }
            else if (type == typeof(Int64) || type == typeof(Nullable<Int64>))
            {
                val = SqlDbType.BigInt;
            }
            else if (type == typeof(Byte[]))
            {
                val = SqlDbType.Binary;
            }
            else if (type == typeof(Boolean) || type == typeof(Nullable<Boolean>))
            {
                val = SqlDbType.Bit;
            }
            else if (type == typeof(DateTime) || type == typeof(Nullable<DateTime>))
            {
                val = SqlDbType.DateTime;
            }
            else if (type == typeof(Decimal) || type == typeof(Nullable<Decimal>))
            {
                val = SqlDbType.Decimal;
            }
            else if (type == typeof(Int32) || type == typeof(Nullable<Int32>))
            {
                val = SqlDbType.Int;
            }
            else if (type == typeof(Int16) || type == typeof(Nullable<Int16>))
            {
                val = SqlDbType.SmallInt;
            }
            else if (type == typeof(Byte) || type == typeof(Nullable<Byte>))
            {
                val = SqlDbType.TinyInt;
            }
            else if (type == typeof(Single) || type == typeof(Nullable<Single>))
            {
                val = SqlDbType.Real;
            }
            else if (type == typeof(Double) || type == typeof(Nullable<Double>))
            {
                val = SqlDbType.Float;
            }
            else if (type == typeof(TimeSpan) || type == typeof(Nullable<TimeSpan>))
            {
                val = SqlDbType.Time;
            }
            else if (type == typeof(Guid) || type == typeof(Nullable<Guid>))
            {
                val = SqlDbType.UniqueIdentifier;
            }
            else
            {
                throw new Exception("Unmapped type");
            }


            // Please refer to the following document to add other types
            // http://msdn.microsoft.com/en-us/library/ms131092.aspx
            return val;
        }
        public static string GetFullSqlTypeName(string typeName, int max_length, int precision, int scale)
        {
            switch (typeName.ToLower())
            {
                case "varchar":
                case "nvarchar":
                case "varbinary":
                case "char":
                case "nchar":
                case "binary":
                    return $"{typeName}({(max_length == -1 ? "max" : (typeName.ToLower().StartsWith("n") ? max_length / 2 : max_length).ToString())})";
                case "decimal":
                case "numeric":
                    return $"{typeName}({precision},{scale})";
                case "float":
                    return precision == 53 ? typeName : $"{typeName}({precision})";
                case "real":
                case "datetime2":
                case "datetimeoffset":
                    return $"{typeName}({precision})";
                default:
                    return typeName;
            }
        }

    }
}
