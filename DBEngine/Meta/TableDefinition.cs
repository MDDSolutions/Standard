using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MDDDataAccess
{
    public class TableDefinition
    {
        public TableDefinition()
        {

        }
        public TableDefinition(string schema, string name)
        {
            Name = name;
            SchemaName = schema;
        }
        public string Name { get; set; }
        public string FullName => $"[{SchemaName}].[{Name}]";
        public int ObjectID { get; set; }
        public string SchemaName { get; set; }
        public long TableRowCount { get; set; }
        public List<ColumnDefinition> Columns { get; set; }


        public async Task LoadColumns(DBEngine dbengine, CancellationToken token)
        {
            var systemcolumns = new string[] {"created_date", "modified_date" };
            foreach (var item in await dbengine.SqlRunQueryWithResultsAsync<ColumnDefinition>(
                $@"SELECT c.object_id AS TableObjectID,
               c.name AS Name,
               st.name AS DataType,
               c.column_id AS ColumnID,
               c.max_length AS MaxLength,
               c.precision AS Precision,
               c.scale AS Scale,
               c.is_nullable AS IsNullable,
               c.is_identity AS IsIdentity,
               c.is_computed AS IsComputed,
               CAST(CASE WHEN pkc.column_id IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS IsPrimaryKey,
               CAST(CASE WHEN dc.column_id IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS HasDefault
        FROM sys.columns c
        JOIN sys.tables t ON c.object_id = t.object_id
        JOIN sys.types st ON st.user_type_id = c.system_type_id
        LEFT JOIN
        (
            SELECT ic.object_id,
                   ic.column_id
            FROM sys.index_columns ic
            JOIN sys.indexes i
                ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.key_constraints kc
                ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
            WHERE kc.type = 'PK'
        ) pkc
            ON c.object_id = pkc.object_id
               AND c.column_id = pkc.column_id
        LEFT JOIN
        (
            SELECT c.object_id,
                   c.column_id
            FROM sys.default_constraints dc
            JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
        ) dc
            ON c.object_id = dc.object_id
               AND c.column_id = dc.column_id
        WHERE t.type = 'U'
            AND t.object_id = OBJECT_ID('{FullName}')
        ORDER BY c.column_id;",
                false, token, -1, null).ConfigureAwait(false))
            {
                item.Table = this;
                item.SqlDbType = DBEngine.GetSqlType(item.DataType);
                if (Columns == null) Columns = new List<ColumnDefinition>();
                if (systemcolumns.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    item.IsSystem = true;
                }
                Columns.Add(item);
            }
        }
        public async Task LoadIndexes(DBEngine dbengine, CancellationToken token)
        {
            var indexLoads = await dbengine.SqlRunQueryWithResultsAsync<IndexLoad>(
                $@"SELECT 
                        i.name AS IndexName, 
                        t.object_id AS TableObjectID, 
                        ic.column_id AS ColumnID, 
                        i.is_unique AS IsUnique, 
                        i.is_primary_key AS IsPrimaryKey,
                        ic.is_included_column AS IsIncludedColumn,
                        ic.index_column_id AS IndexColumnID,
                        i.type AS IndexType,
                        i.filter_definition AS FilterDefinition
                    FROM sys.indexes i 
                    JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id 
                    JOIN sys.tables t ON i.object_id = t.object_id 
                    WHERE t.object_id = OBJECT_ID('{FullName}')   
                    ORDER BY t.object_id, i.name, ic.index_column_id",
                false, token);

            IndexDefinition currentIndex = null;

            foreach (var indexLoad in indexLoads)
            {
                if (currentIndex == null || currentIndex.Name != indexLoad.IndexName)
                {
                    currentIndex = new IndexDefinition
                    {
                        Name = indexLoad.IndexName,
                        Table = this,
                        IsUnique = indexLoad.IsUnique,
                        IsPrimaryKey = indexLoad.IsPrimaryKey,
                        IsClustered = indexLoad.IndexType == 1,
                        FilterDefinition = indexLoad.FilterDefinition,
                        Columns = new List<IndexColumnDefinition>()
                    };

                    if (Indexes == null) Indexes = new List<IndexDefinition>();
                    Indexes.Add(currentIndex);
                }

                var column = Columns.FirstOrDefault(c => c.ColumnID == indexLoad.ColumnID);
                if (column != null)
                {
                    var indexColumn = new IndexColumnDefinition
                    {
                        Column = column,
                        IsIncluded = indexLoad.IsIncludedColumn,
                        IndexColumnID = indexLoad.IndexColumnID,
                        FirstOrderColumn = indexLoad.IndexColumnID == 1 // Assuming first order column has IndexColumnID = 1
                    };
                    currentIndex.Columns.Add(indexColumn);
                }
                else
                {
                    throw new Exception($"Column not found for index {indexLoad.IndexName} on table {FullName}");
                }
            }
        }



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
            cmdText.AppendLine($"WHERE {string.Join(" AND ", FullConcurrencyWhereClauses)};");
            cmdText.AppendLine($"SELECT {GetSelectList("t")} FROM {FullName} t JOIN @KeyTable k ON {string.Join(" AND ", finalselectkeys.Select(c => $"t.[{c.Name}] = k.[{c.Name}]"))};");

            var cmd = new SqlCommand(cmdText.ToString());

            foreach (var c in updateColumns)
            {
                var p = new SqlParameter($"@Current_{c.CodeFriendlyName}", c.SqlDbType, c.MaxLength, c.Name);
                p.SourceVersion = DataRowVersion.Current;
                cmd.Parameters.Add(p);
            }
            cmd.Parameters.AddRange(FullConcurrencyWhereParameters.ToArray());


            return cmd;
        }
        public SqlCommand GetDeleteCommand()
        {
            var cmd = new SqlCommand($"DELETE FROM {FullName} WHERE {string.Join(" AND ", FullConcurrencyWhereClauses)};");
            cmd.Parameters.AddRange(FullConcurrencyWhereParameters.ToArray());
            return cmd;
        }
        public List<ColumnDefinition> FullConcurrencyWhereColumns => Columns.Where(c => !c.IsComputed && !c.IsReadOnly).ToList();
        public List<string> FullConcurrencyWhereClauses => FullConcurrencyWhereColumns.Select(c =>
        {
            if (c.IsNullable)
                return $"((@IsNull_{c.CodeFriendlyName} = 1 AND [{c.Name}] IS NULL) OR ([{c.Name}] = @Original_{c.CodeFriendlyName}))";
            return $"[{c.Name}] = @Original_{c.CodeFriendlyName}";
        }).ToList();
        public List<SqlParameter> FullConcurrencyWhereParameters
        {
            get
            {
                var parameters = new List<SqlParameter>();
                foreach (var c in FullConcurrencyWhereColumns)
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
    public class IndexLoad
    {
        public string IndexName { get; set; }
        public int TableObjectID { get; set; }
        public int ColumnID { get; set; }
        public bool IsUnique { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsIncludedColumn { get; set; }
        public int IndexColumnID { get; set; }
        public byte IndexType { get; set; }
        public string FilterDefinition { get; set; }
    }
}
