using System;
using System.Collections.Generic;
using System.Text;

namespace MDDDataAccess
{
    public interface IColumnDefinition
    {
        string Name { get; set; }
        bool IsIdentity { get; set; }
        bool IsPrimaryKey { get; set; }
        bool HasDefault { get; set; }
        bool IsComputed { get; set; }
        bool IsNullable { get; set; }
    }
    public class ColumnDefinition : IColumnDefinition
    {
        public string Name { get; set; }
        public bool IsIdentity { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool HasDefault { get; set; }
        public bool IsComputed { get; set; }
        public bool IsNullable { get; set; }
    }
}
