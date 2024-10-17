using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MDDDataAccess
{
    public class RelationshipDefinition
    {
        public string Name { get; set; }
        public TableDefinition ParentTable { get; set; }
        public TableDefinition ChildTable { get; set; }
        public List<ColumnDefinition> ParentColumns { get; set; }
        public List<ColumnDefinition> ChildColumns { get; set; }
        override public string ToString()
        {
            if (ParentTable != null && ChildTable != null)
            {
                if (ParentColumns != null && ChildColumns != null)
                {
                    return $"{ParentTable.FullName} ({string.Join(", ", ParentColumns.Select(x => x.Name))}) -> {ChildTable.FullName} ({string.Join(", ", ChildColumns.Select(x => x.Name))})";
                }
                return $"{ParentTable.FullName} -> {ChildTable.FullName}";
            }
            else
            {
                return Name;
            }
        }
    }
}
