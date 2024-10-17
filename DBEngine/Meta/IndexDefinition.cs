using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MDDDataAccess
{
    public class IndexDefinition
    {
        public string Name { get; set; }
        public TableDefinition Table { get; set; }
        public List<IndexColumnDefinition> Columns { get; set; } = new List<IndexColumnDefinition>();
        public bool IsUnique { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsClustered { get; set; }
        public string FilterDefinition { get; set; }
        public override string ToString()
        {
            if (Table == null) return "Table is null";
            return $"{Table.FullName}: {(IsClustered ? "CL" : "NC")} {(IsUnique ? "UQ" : "NU")} {(FilterDefinition != null ? "F" : "")} ({string.Join(", ", Columns.Where(c => !c.IsIncluded).OrderBy(c => c.IsIncluded).Select(c => c.Column.Name))})";
        }
    }
}
