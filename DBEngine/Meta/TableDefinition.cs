using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MDDDataAccess
{
    public class TableDefinition
    {
        public string Name { get; set; }
        public string FullName => $"[{SchemaName}].[{Name}]";
        public int ObjectID { get; set; }
        public string SchemaName { get; set; }
        public long TableRowCount { get; set; }
        public List<ColumnDefinition> Columns { get; set; }
        public List<RelationshipDefinition> Relationships { get; set; }
        public List<IndexDefinition> Indexes { get; set; }
        override public string ToString() => FullName;
        public SqlCommand GetSelectCommand(ViewModelQueryType vmqType = ViewModelQueryType.All, int top = 0)
        {
            switch (vmqType)
            {
                case ViewModelQueryType.FullText:
                    var ftcmd = new SqlCommand($"SELECT {(top > 0 ? $"TOP ({top})" : "")} {GetSelectList("t")} FROM {FullName} t WHERE {string.Join(" OR ", fulltextfiltercolumns.Select(c => $"t.[{c.Name}] LIKE @{c.CodeFriendlyName}"))}");
                    foreach (var c in fulltextfiltercolumns)
                    {
                        var p = new SqlParameter($"@{c.CodeFriendlyName}", SqlDbType.VarChar, c.MaxLength);
                        p.SourceColumn = c.Name;
                        ftcmd.Parameters.Add(p);
                    }
                    return ftcmd;
                case ViewModelQueryType.Indexed:
                    var icols = Indexes
                                .SelectMany(index => index.Columns)
                                .Where(column => column.FirstOrderColumn)
                                .Select(column => new { column.Column.Name, column.Column.CodeFriendlyName })
                                .Distinct()
                                .ToList();
                    var icmd = new SqlCommand($"SELECT {(top > 0 ? $"TOP ({top})" : "")} {GetSelectList("t")} FROM {FullName} t WHERE {string.Join(" OR ", icols.Select(c => $"t.[{c.Name}] LIKE @{c.CodeFriendlyName}"))}");
                    foreach (var c in fulltextfiltercolumns)
                    {
                        var p = new SqlParameter($"@{c.CodeFriendlyName}", SqlDbType.VarChar, c.MaxLength);
                        p.SourceColumn = c.Name;
                        icmd.Parameters.Add(p);
                    }
                    return icmd;
                case ViewModelQueryType.All:
                default:
                    return new SqlCommand($"SELECT {(top > 0 ? $"TOP ({top})" : "")} {GetSelectList("t")} FROM {FullName} t;");
            }
        }
        public SqlCommand GetSelectCommandByID()
        {
            var keyColumns = Columns.Where(c => c.IsPrimaryKey).ToList();
            var cmd = new SqlCommand($"SELECT {GetSelectList("t")} FROM {FullName} t WHERE {string.Join(" AND ", keyColumns.Select(c => $"t.[{c.Name}] = @Current_{c.CodeFriendlyName}"))};");
            foreach (var c in keyColumns)
            {
                var p = new SqlParameter($"@Current_{c.CodeFriendlyName}", c.SqlDbType, c.MaxLength);
                p.SourceColumn = c.Name;
                cmd.Parameters.Add(p);
            }
            return cmd;
        }
        public SqlCommand GetInsertCommand()
        {
            var insertColumns = Columns.Where(c => !c.IsIdentity && !c.IsComputed && !c.IsReadOnly && !c.IsSystem && (!c.IsPrimaryKey || !c.HasDefault)).ToList();
            var insertkeys = Columns.Where(c => c.IsPrimaryKey).ToList();

            var cmdText = new StringBuilder();
            cmdText.AppendLine($"DECLARE @KeyTable TABLE ({string.Join(", ", insertkeys.Select(c => $"[{c.Name}] {DBEngine.GetFullSqlTypeName(c.DataType, c.MaxLength, c.Precision, c.Scale)}"))});");
            cmdText.AppendLine($"INSERT INTO {FullName} ({string.Join(", ", insertColumns.Select(c => $"[{c.Name}]"))})");
            cmdText.AppendLine($"OUTPUT {string.Join(", ", insertkeys.Select(c => $"inserted.[{c.Name}]"))} INTO @KeyTable");
            cmdText.AppendLine($"VALUES ({string.Join(", ", insertColumns.Select(c => $"@Current_{c.CodeFriendlyName}"))});");
            cmdText.AppendLine($"SELECT {GetSelectList("t")} FROM {FullName} t JOIN @KeyTable k ON {string.Join(" AND ", insertkeys.Select(c => $"t.[{c.Name}] = k.[{c.Name}]"))};");
            var cmd = new SqlCommand(cmdText.ToString());

            //var cmd = new SqlCommand($@"
            //    INSERT INTO {FullName} ({string.Join(", ", insertColumns.Select(c => c.Name))}) 
            //    OUTPUT inserted.*
            //    VALUES ({string.Join(", ", insertColumns.Select(c => $"@Current_{c.CodeFriendlyName}"))});

            //    ");
            foreach (var c in insertColumns)
            {
                var p = new SqlParameter($"@Current_{c.CodeFriendlyName}", c.SqlDbType, c.MaxLength, c.Name);
                p.SourceVersion = DataRowVersion.Current;
                cmd.Parameters.Add(p);
            }
            return cmd;
        }
        public SqlCommand GetUpdateCommand()
        {
            var updateColumns = Columns.Where(c => !c.IsIdentity && !c.IsPrimaryKey && !c.IsComputed && !c.IsReadOnly && !c.IsSystem).ToList();
            var setClauses = updateColumns.Select(c => $"[{c.Name}] = @Current_{c.CodeFriendlyName}").ToList();
            var finalselectkeys = Columns.Where(c => c.IsPrimaryKey).ToList();


            var cmdText = new StringBuilder();
            cmdText.AppendLine($"DECLARE @KeyTable TABLE ({string.Join(", ", finalselectkeys.Select(c => $"[{c.Name}] {DBEngine.GetFullSqlTypeName(c.DataType, c.MaxLength, c.Precision, c.Scale)}"))});");
            cmdText.AppendLine($"UPDATE {FullName} SET {string.Join(", ", setClauses)}");
            cmdText.AppendLine($"OUTPUT {string.Join(", ", finalselectkeys.Select(c => $"inserted.[{c.Name}]"))} INTO @KeyTable");
            cmdText.AppendLine($"WHERE {string.Join(" AND ", fullConcurrencyWhereClauses)};");
            cmdText.AppendLine($"SELECT {GetSelectList("t")} FROM {FullName} t JOIN @KeyTable k ON {string.Join(" AND ", finalselectkeys.Select(c => $"t.[{c.Name}] = k.[{c.Name}]"))};");

            var cmd = new SqlCommand(cmdText.ToString());

            foreach (var c in updateColumns)
            {
                var p = new SqlParameter($"@Current_{c.CodeFriendlyName}", c.SqlDbType, c.MaxLength, c.Name);
                p.SourceVersion = DataRowVersion.Current;
                cmd.Parameters.Add(p);
            }
            cmd.Parameters.AddRange(fullConcurrencyWhereParameters.ToArray());


            return cmd;
        }
        public SqlCommand GetDeleteCommand()
        {
            var cmd = new SqlCommand($"DELETE FROM {FullName} WHERE {string.Join(" AND ", fullConcurrencyWhereClauses)};");
            cmd.Parameters.AddRange(fullConcurrencyWhereParameters.ToArray());
            return cmd;
        }
        private List<ColumnDefinition> fullConcurrencyWhereColumns => Columns.Where(c => !c.IsComputed && !c.IsReadOnly).ToList();
        private List<string> fullConcurrencyWhereClauses => fullConcurrencyWhereColumns.Select(c =>
        {
            if (c.IsNullable)
                return $"((@IsNull_{c.CodeFriendlyName} = 1 AND [{c.Name}] IS NULL) OR ([{c.Name}] = @Original_{c.CodeFriendlyName}))";
            return $"[{c.Name}] = @Original_{c.CodeFriendlyName}";
        }).ToList();
        private List<SqlParameter> fullConcurrencyWhereParameters
        {
            get
            {
                var parameters = new List<SqlParameter>();
                foreach (var c in fullConcurrencyWhereColumns)
                {
                    var p = new SqlParameter($"@Original_{c.CodeFriendlyName}", c.SqlDbType, c.MaxLength, c.Name);
                    p.SourceVersion = DataRowVersion.Original;
                    parameters.Add(p);

                    if (c.IsNullable)
                    {
                        var isNullParam = new SqlParameter($"@IsNull_{c.CodeFriendlyName}", SqlDbType.Int, 0, c.Name);
                        isNullParam.SourceVersion = DataRowVersion.Original;
                        isNullParam.SourceColumnNullMapping = true;
                        isNullParam.Value = 1;
                        parameters.Add(isNullParam);
                    }
                }
                return parameters;
            }
        }
        private string GetSelectList(string tablealias = null) => string.Join(", ", Columns.Select(c => $"{(tablealias == null ? "" : $"{tablealias}.")}[{c.Name}]"));
        private List<ColumnDefinition> fulltextfiltercolumns 
        {
            get
            {
                return Columns.Where(c =>
                {
                    var type = DBEngine.GetClrType(c.SqlDbType);
                    return !c.IsSystem && !c.IsHiddenForDisplay && type != typeof(byte[]) && type != typeof(bool);
                }).ToList();
            }

        }
    }
}
