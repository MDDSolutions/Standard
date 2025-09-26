using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
            var r = SqlRunQueryWithResults<SQLColumn>(query, false, -1, null, GetParameter(() => objectname));
            AllowAdHoc = allowadhoc;
            return r;
        }
        public static SqlDbType GetSqlType(Type type)
        {
            switch (type)
            {
                case Type t when t == typeof(string):
                    return SqlDbType.VarChar;
                case Type t when t == typeof(long) || t == typeof(long?):
                    return SqlDbType.BigInt;
                case Type t when t == typeof(byte[]):
                    return SqlDbType.Binary;
                case Type t when t == typeof(bool) || t == typeof(bool?):
                    return SqlDbType.Bit;
                case Type t when t == typeof(DateTime) || t == typeof(DateTime?):
                    return SqlDbType.DateTime;
                case Type t when t == typeof(decimal) || t == typeof(decimal?):
                    return SqlDbType.Decimal;
                case Type t when t == typeof(int) || t == typeof(int?):
                    return SqlDbType.Int;
                case Type t when t == typeof(short) || t == typeof(short?):
                    return SqlDbType.SmallInt;
                case Type t when t == typeof(byte) || t == typeof(byte?):
                    return SqlDbType.TinyInt;
                case Type t when t == typeof(float) || t == typeof(float?):
                    return SqlDbType.Real;
                case Type t when t == typeof(double) || t == typeof(double?):
                    return SqlDbType.Float;
                case Type t when t == typeof(TimeSpan) || t == typeof(TimeSpan?):
                    return SqlDbType.Time;
                case Type t when t == typeof(Guid) || t == typeof(Guid?):
                    return SqlDbType.UniqueIdentifier;
                default:
                    throw new Exception("Unmapped type");
            }

            // Please refer to the following document to add other types
            // http://msdn.microsoft.com/en-us/library/ms131092.aspx
        }
        public static SqlDbType GetSqlType(string sqltypename)
        {
            switch (sqltypename.ToLower())
            {
                case "bigint":
                    return SqlDbType.BigInt;
                case "binary":
                case "varbinary":
                    return SqlDbType.Binary;
                case "bit":
                    return SqlDbType.Bit;
                case "char":
                case "nchar":
                    return SqlDbType.Char;
                case "date":
                    return SqlDbType.Date;
                case "datetime":
                    return SqlDbType.DateTime;
                case "datetime2":
                    return SqlDbType.DateTime2;
                case "datetimeoffset":
                    return SqlDbType.DateTimeOffset;
                case "decimal":
                case "numeric": // SqlDbType.Numeric is an alias for SqlDbType.Decimal
                    return SqlDbType.Decimal;
                case "float":
                    return SqlDbType.Float;
                case "image":
                    return SqlDbType.Image;
                case "int":
                    return SqlDbType.Int;
                case "money":
                case "smallmoney":
                    return SqlDbType.Money;
                case "ntext":
                case "text":
                    return SqlDbType.Text;
                case "nvarchar":
                case "varchar":
                    return SqlDbType.VarChar;
                case "real":
                    return SqlDbType.Real;
                case "smalldatetime":
                    return SqlDbType.SmallDateTime;
                case "smallint":
                    return SqlDbType.SmallInt;
                case "time":
                    return SqlDbType.Time;
                case "timestamp":
                    return SqlDbType.Timestamp;
                case "tinyint":
                    return SqlDbType.TinyInt;
                case "uniqueidentifier":
                    return SqlDbType.UniqueIdentifier;
                case "xml":
                    return SqlDbType.Xml;
                case "sql_variant":
                    return SqlDbType.Variant;
                default:
                    throw new ArgumentException($"Unmapped SQL type: {sqltypename}");
            }
        }
        public static string GetFullSqlTypeName(string sqltypename, int max_length, int? precision = null, int? scale = null)
        {
            switch (sqltypename.ToLower())
            {
                case "varchar":
                case "nvarchar":
                case "varbinary":
                case "char":
                case "nchar":
                case "binary":
                    return $"{sqltypename}({(max_length == -1 ? "max" : (sqltypename.ToLower().StartsWith("n") ? max_length / 2 : max_length).ToString())})";
                case "decimal":
                case "numeric":
                    if (precision == null) precision = 38;
                    if (scale == null) scale = 38;
                    return $"{sqltypename}({precision},{scale})";
                case "float":
                    if (precision == null) precision = 53;
                    return precision == 53 ? sqltypename : $"{sqltypename}({precision})";
                case "real":
                    return "real";
                case "datetime2":
                case "datetimeoffset":
                    if (precision == null) precision = 7;
                    return $"{sqltypename}({precision})";
                default:
                    return sqltypename;
            }
        }
        public static string GetFullSqlTypeName(Type clrType, int max_length = -1, int? precision = null, int? scale = null)
        {
            SqlDbType sqlDbType = GetSqlType(clrType);

            switch (sqlDbType)
            {
                case SqlDbType.VarChar:
                case SqlDbType.NVarChar:
                case SqlDbType.VarBinary:
                case SqlDbType.Char:
                case SqlDbType.NChar:
                case SqlDbType.Binary:
                    return $"{sqlDbType}({(max_length == -1 ? "max" : (sqlDbType == SqlDbType.NVarChar || sqlDbType == SqlDbType.NChar ? max_length / 2 : max_length).ToString())})";
                case SqlDbType.Decimal:
                    if (precision == null) precision = 38;
                    if (scale == null) scale = 38;
                    return $"{sqlDbType}({precision},{scale})";
                case SqlDbType.Float:
                    if (precision == null) precision = 53;
                    return precision == 53 ? sqlDbType.ToString() : $"{sqlDbType}({precision})";
                case SqlDbType.Real:
                case SqlDbType.DateTime2:
                case SqlDbType.DateTimeOffset:
                case SqlDbType.Time:
                    if (precision == null) precision = 7;
                    return $"{sqlDbType}({precision})";
                default:
                    return sqlDbType.ToString();
            }
        }
        public static Type GetClrType(SqlDbType sqlDbType)
        {
            switch (sqlDbType)
            {
                case SqlDbType.BigInt:
                    return typeof(long);
                case SqlDbType.Binary:
                case SqlDbType.VarBinary:
                    return typeof(byte[]);
                case SqlDbType.Bit:
                    return typeof(bool);
                case SqlDbType.Char:
                case SqlDbType.NChar:
                case SqlDbType.NText:
                case SqlDbType.NVarChar:
                case SqlDbType.Text:
                case SqlDbType.VarChar:
                    return typeof(string);
                case SqlDbType.Date:
                case SqlDbType.DateTime:
                case SqlDbType.DateTime2:
                case SqlDbType.SmallDateTime:
                case SqlDbType.DateTimeOffset:
                    return typeof(DateTime);
                case SqlDbType.Decimal:
                case SqlDbType.Money:
                case SqlDbType.SmallMoney:
                    return typeof(decimal);
                case SqlDbType.Float:
                    return typeof(double);
                case SqlDbType.Int:
                    return typeof(int);
                case SqlDbType.Real:
                    return typeof(float);
                case SqlDbType.SmallInt:
                    return typeof(short);
                case SqlDbType.Time:
                    return typeof(TimeSpan);
                case SqlDbType.Timestamp:
                    return typeof(byte[]);
                case SqlDbType.TinyInt:
                    return typeof(byte);
                case SqlDbType.UniqueIdentifier:
                    return typeof(Guid);
                case SqlDbType.Xml:
                    return typeof(string);
                default:
                    throw new ArgumentException($"Unmapped SQL type: {sqlDbType}");
            }
        }
        public static Type GetClrType(string sqlTypeName)
        {
            switch (sqlTypeName.ToLower())
            {
                case "bigint":
                    return typeof(long);
                case "binary":
                case "varbinary":
                case "timestamp":
                case "image": // Added
                    return typeof(byte[]);
                case "bit":
                    return typeof(bool);
                case "char":
                case "nchar":
                case "ntext":
                case "nvarchar":
                case "text":
                case "varchar":
                    return typeof(string);
                case "xml":
                    return typeof(System.Xml.Linq.XDocument); // Changed for structured XML
                case "date":
                case "datetime":
                case "datetime2":
                case "smalldatetime":
                    return typeof(DateTime);
                case "datetimeoffset":
                    return typeof(DateTimeOffset); // Fixed
                case "decimal":
                case "numeric":
                case "money":
                case "smallmoney":
                    return typeof(decimal);
                case "float":
                    return typeof(double);
                case "int":
                    return typeof(int);
                case "real":
                    return typeof(float);
                case "smallint":
                    return typeof(short);
                case "time":
                    return typeof(TimeSpan);
                case "tinyint":
                    return typeof(byte);
                case "uniqueidentifier":
                    return typeof(Guid);
                //case "geometry":
                //    return typeof(Microsoft.SqlServer.Types.SqlGeometry);
                //case "geography":
                //    return typeof(Microsoft.SqlServer.Types.SqlGeography);
                //case "hierarchyid":
                //    return typeof(Microsoft.SqlServer.Types.SqlHierarchyId);
                case "sql_variant":
                    return typeof(object);
                default:
                    throw new ArgumentException($"Unmapped SQL type: {sqlTypeName}");
            }
        }


        public static string GetClrTypeString(Type type, bool nullable)
        {
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Boolean: return $"bool{(nullable ? "?" : "")}";
                case TypeCode.Byte: return $"byte{(nullable ? "?" : "")}";
                case TypeCode.Char: return $"char{(nullable ? "?" : "")}";
                case TypeCode.DateTime: return $"DateTime{(nullable ? "?" : "")}";
                case TypeCode.Decimal: return $"decimal{(nullable ? "?" : "")}";
                case TypeCode.Double: return $"double{(nullable ? "?" : "")}";
                case TypeCode.Int16: return $"short{(nullable ? "?" : "")}";
                case TypeCode.Int32: return $"int{(nullable ? "?" : "")}";
                case TypeCode.Int64: return $"long{(nullable ? "?" : "")}";
                case TypeCode.Single: return $"float{(nullable ? "?" : "")}";
                case TypeCode.String: return "string";
                default: return type.Name;
            }
        }
        public IList<SQLColumn> GetResultSetSchema(string tsql)
        {
            var columns = new List<SQLColumn>();

            using (var connection = getconnection())
            {
                using (var command = new SqlCommand(tsql, connection))
                {
                    using (var reader = command.ExecuteReader())
                    {
                        var schemaTable = reader.GetSchemaTable();
                        foreach (DataRow row in schemaTable.Rows)
                        {
                            columns.Add(SQLColumn.FromSchemaTableRow(row));
                        }
                    }
                }
            }

            return columns;
        }



        public async Task<IList<SQLColumn>> GetResultSetSchemaAsync(string tsql, CancellationToken token)
        {
            var columns = new List<SQLColumn>();

            using (var connection = await getconnectionasync(token).ConfigureAwait(false))
            {
                using (var command = new SqlCommand(tsql, connection))
                {
                    if (token != CancellationToken.None) command.CommandTimeout = 0;
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        var schemaTable = reader.GetSchemaTable();
                        foreach (DataRow row in schemaTable.Rows)
                        {
                            columns.Add(SQLColumn.FromSchemaTableRow(row));
                        }
                    }
                }
            }

            return columns;
        }
    }
}
