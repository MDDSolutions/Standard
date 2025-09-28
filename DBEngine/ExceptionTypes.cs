using System;
using System.Collections.Generic;
using System.Text;
using static MDDDataAccess.DBEngine;

namespace MDDDataAccess
{
    [Serializable]
    public class DBEngineMappingException : Exception
    {
        public PropertyMapEntry PropertyMapEntry { get; set; }
        public DBEngineMappingException(
            PropertyMapEntry propertyMapEntry,
            string message,
            Exception innerException)
            : base(message, innerException)
        {
            PropertyMapEntry = propertyMapEntry;
        }
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine(base.ToString());
            sb.AppendLine($"Property: {PropertyMapEntry.Property.Name}");
            sb.AppendLine($"PropertyType: {PropertyMapEntry.Property.PropertyType.FullName}");
            sb.AppendLine($"ColumnName: {PropertyMapEntry.ColumnName}");
            sb.AppendLine($"ObjectType: {PropertyMapEntry.Property.DeclaringType.Name}");
            sb.AppendLine($"ReaderType: {PropertyMapEntry.ReaderType.Name}");
            return sb.ToString();
        }
    }
    [Serializable]
    public class DBEngineColumnRequiredException : Exception
    {
        public string PropertyName { get; }
        public string PropertyType { get; }
        public string ColumnName { get; }
        public string ObjectType { get; }
        public bool IsOptional { get; }

        public DBEngineColumnRequiredException(
            string propertyName,
            string propertyType,
            string columnName,
            string objectType,
            bool isOptional,
            string message)
            : base(message)
        {
            PropertyName = propertyName;
            PropertyType = propertyType;
            ColumnName = columnName;
            ObjectType = objectType;
            IsOptional = isOptional;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine(base.ToString());
            sb.AppendLine($"Property: {PropertyName}");
            sb.AppendLine($"PropertyType: {PropertyType}");
            sb.AppendLine($"ColumnName: {ColumnName}");
            sb.AppendLine($"ObjectType: {ObjectType}");
            sb.AppendLine($"IsOptional: {IsOptional}");
            return sb.ToString();
        }
    }
    [Serializable]
    public class DBEnginePostMappingException<T> : Exception
    {
        public T TargetObject { get; }
        public string PropertyName { get; }
        public string PropertyType { get; }
        public string ReaderType { get; }
        public string ObjectValues { get; }

        public DBEnginePostMappingException(
            T targetObject,
            string propertyName,
            string propertyType,
            string readerType,
            string objectValues,
            string message,
            Exception innerException)
            : base(message, innerException)
        {
            TargetObject = targetObject;
            PropertyName = propertyName;
            PropertyType = propertyType;
            ReaderType = readerType;
            ObjectValues = objectValues;
        }
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine(base.ToString());
            sb.AppendLine($"Property: {PropertyName}");
            sb.AppendLine($"PropertyType: {PropertyType}");
            sb.AppendLine($"ReaderType: {ReaderType}");
            sb.AppendLine($"ObjectValues: {ObjectValues}");
            return sb.ToString();
        }
    }
    [Serializable]
    public class DBEngineConcurrencyMismatchException : Exception
    {
        public object KeyValue { get; set; }
        public List<ConcurrencyMismatchRecord> MismatchRecords { get; set; }
        public DBEngineConcurrencyMismatchException(string message, object keyvalue, List<ConcurrencyMismatchRecord> records) : base(message)
        {
            KeyValue = keyvalue;
            MismatchRecords = records;
        }
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine(base.ToString());
            if (MismatchRecords != null)
                foreach (var item in MismatchRecords)
                    sb.AppendLine(item.ToString());
            return sb.ToString();
        }
    }
    public class ConcurrencyMismatchRecord
    {
        public string PropertyName { get; set; }
        public object AppValue { get; set; }
        public object DBValue { get; set; }
        public override string ToString()
        {
            return $"Property: {PropertyName} AppValue: {AppValue} DBValue: {DBValue}";
        }
    }
}
