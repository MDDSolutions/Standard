using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace MDDFoundation
{
    public class BitwiseEnum<T> : IXmlSerializable where T : Enum
    {
        private static Dictionary<T, int> _valuesMap;
        private static readonly int allvalues = 0;
        static BitwiseEnum()
        {
            _valuesMap = Enum.GetValues(typeof(T))
                             .Cast<T>()
                             .ToDictionary(v => v, v => 1 << (int)(object)v);
            allvalues = _valuesMap.Values.Sum();
        }
        public static int AllPossibleValues => allvalues;
        public BitwiseEnum()
        {
            if (_valuesMap == null)
            {
                _ = AllPossibleValues;
            }
            _value = 0;
        }
        public BitwiseEnum(int combinedValue) => _value = combinedValue;
        private int _value;
        public int Value => _value;
        public static implicit operator BitwiseEnum<T>(int value)
        {
            if ((value & AllPossibleValues) != value)
            {
                throw new ArgumentException($"Invalid integer value '{value}' for BitwiseEnum<{typeof(T).Name}>.");
            }
            return new BitwiseEnum<T> (value);
        }
        //public static bool operator ==(BitwiseEnum<T> left, BitwiseEnum<T> right) => (left == null ? -1 : left.Value) == (right == null ? -1 : right.Value);
        //public static bool operator !=(BitwiseEnum<T> left, BitwiseEnum<T> right) => (left == null ? -1 : left.Value) != (right == null ? -1 : right.Value);
        //public override bool Equals(object obj)
        //{
        //    if (obj is BitwiseEnum<T> other)
        //    {
        //        return this == other;
        //    }
        //    return false;
        //}
        //public override int GetHashCode()
        //{
        //    return Value.GetHashCode();
        //}
        public bool HasValue(T value)
        {
            if (!_valuesMap.ContainsKey(value))
                throw new ArgumentException("The provided enum value is not supported.");

            int numericValue = _valuesMap[value];
            return (Value & numericValue) == numericValue;
        }
        public BitwiseEnum<T> AddValue(T value)
        {
            if (!_valuesMap.ContainsKey(value))
                throw new ArgumentException("The provided enum value is not supported.");

            int numericValue = _valuesMap[value];
            return new BitwiseEnum<T>(Value | numericValue);
        }
        public BitwiseEnum<T> RemoveValue(T value)
        {
            if (!_valuesMap.ContainsKey(value))
                throw new ArgumentException("The provided enum value is not supported.");

            int numericValue = _valuesMap[value];
            return new BitwiseEnum<T> (Value & ~numericValue);
        }
        public IEnumerable<T> GetContainedValues()
        {
            return _valuesMap.Where(kv => (Value & kv.Value) == kv.Value).Select(kv => kv.Key);
        }
        public int Count()
        {
            int count = 0;
            int value = Value;
            while (value != 0)
            {
                value &= (value - 1);
                count++;
            }
            return count;
        }
        public override string ToString()
        {
            if (Value == 0) return "None";
            if (Value == AllPossibleValues) return "All";
            var selectedValues = _valuesMap.Where(kv => (Value & kv.Value) == kv.Value).Select(kv => kv.Key);
            return string.Join(", ", selectedValues);
        }
        public XmlSchema GetSchema() => null;
        public void ReadXml(XmlReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));

            reader.ReadStartElement();

            reader.ReadStartElement("Value");
            _value = int.Parse(reader.ReadString());
            reader.ReadEndElement();

            reader.ReadEndElement();
        }
        public void WriteXml(XmlWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            writer.WriteStartElement("Value");
            writer.WriteString(Value.ToString());
            writer.WriteEndElement();
        }
    }
    public class DaysOfWeek : BitwiseEnum<DayOfWeek>
    {
        public override string ToString() => $"Days: {base.ToString()}";
        public bool ContainsDay(DateTime date) => HasValue(date.DayOfWeek);
        public DaysOfWeek() { }
        public DaysOfWeek(BitwiseEnum<DayOfWeek> bitwiseEnum) : base(bitwiseEnum.Value) { }

        public static implicit operator DaysOfWeek(int value) => new DaysOfWeek((BitwiseEnum<DayOfWeek>)value); // Use the new constructor
        public new DaysOfWeek AddValue(DayOfWeek value) => new DaysOfWeek(base.AddValue(value));
        public new DaysOfWeek RemoveValue(DayOfWeek value) => new DaysOfWeek(base.RemoveValue(value));
        public DaysOfWeek AddString(string str)
        {
            var r = new DaysOfWeek(Value);
            if (str.Contains("M", StringComparison.OrdinalIgnoreCase)) r = r.AddValue(DayOfWeek.Monday);
            if (str.Contains("T", StringComparison.OrdinalIgnoreCase)) r = r.AddValue(DayOfWeek.Tuesday);
            if (str.Contains("W", StringComparison.OrdinalIgnoreCase)) r = r.AddValue(DayOfWeek.Wednesday);
            if (str.Contains("R", StringComparison.OrdinalIgnoreCase)) r = r.AddValue(DayOfWeek.Thursday);
            if (str.Contains("F", StringComparison.OrdinalIgnoreCase)) r = r.AddValue(DayOfWeek.Friday);
            if (str.Contains("SA", StringComparison.OrdinalIgnoreCase)) r = r.AddValue(DayOfWeek.Saturday);
            if (str.Contains("SU", StringComparison.OrdinalIgnoreCase)) r = r.AddValue(DayOfWeek.Sunday);
            return r;
        }
        public static DaysOfWeek FromString(string str) => new DaysOfWeek().AddString(str);
    }
}
