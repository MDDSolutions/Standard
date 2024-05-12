using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace MDDFoundation
{

    public class TimeOfDay : IXmlSerializable
    {
        const int maxvalue = 86400000;
        private int _value;
        public int Value => _value;

        public static implicit operator TimeOfDay(int value)
        {
            if (value < 0 || value >= maxvalue)
                throw new ArgumentOutOfRangeException("Invalid value.");

            return new TimeOfDay(value);
        }
        public byte Hours => (byte)(Value / (3600 * 1000));
        public byte Minutes => (byte)((Value / (60 * 1000)) % 60);
        public byte Seconds => (byte)((Value / 1000) % 60);
        public int Milliseconds => Value % 1000;
        [XmlIgnore]
        public bool IsEndOfDay { get => _value == maxvalue; }
        public TimeOfDay(byte hours, byte minutes, byte seconds, bool endofday = false)
        {
            if (endofday)
            {
                _value = maxvalue;
            }
            else
            {
                if (hours > 23 || minutes > 59 || seconds > 59)
                {
                    throw new ArgumentException("Invalid time.");
                }
                long tval = hours * 3600 * 1000 + minutes * 60 * 1000 + seconds * 1000;
                if (tval > maxvalue || tval < 0)
                    throw new ArgumentOutOfRangeException("value");
                _value = (int)tval;
            }
        }
        public TimeOfDay(int value)
        {
            if (value >= maxvalue)
                _value = maxvalue;
            else
                _value = value; 
        }
        public TimeOfDay()
        {
            _value = 0;
        }
        public override string ToString()
        {
            if (IsEndOfDay)
                return "End of Day";
            return $"{Hours:D2}:{Minutes:D2}:{Seconds:D2}.{Milliseconds:D3}";
        }
        public static TimeOfDay Midnight => new TimeOfDay(0);
        public static TimeOfDay EndOfDay => new TimeOfDay(maxvalue);
        public static bool operator ==(TimeOfDay left, TimeOfDay right) => left.Value == right.Value;
        public static bool operator !=(TimeOfDay left, TimeOfDay right) => left.Value != right.Value;
        public static bool operator <=(TimeOfDay left, TimeOfDay right) => left.Value <= right.Value;
        public static bool operator >=(TimeOfDay left, TimeOfDay right) => left.Value >= right.Value;
        public static bool operator <(TimeOfDay left, TimeOfDay right) => left.Value < right.Value;
        public static bool operator >(TimeOfDay left, TimeOfDay right) => left.Value > right.Value;
        public TimeOfDay AddHours(int hours)
        {
            long newValue = Value + (long)hours * 3600 * 1000;

            if (newValue < 0 || newValue >= maxvalue)
            {
                throw new OverflowException("Adding hours causes overflow.");
            }

            return new TimeOfDay((int)newValue);
        }
        public TimeOfDay AddMinutes(int minutes)
        {
            long newValue = Value + (long)minutes * 60 * 1000;

            if (newValue < 0 || newValue >= maxvalue)
            {
                throw new OverflowException("Adding minutes causes overflow.");
            }

            return new TimeOfDay((int)newValue);
        }
        public TimeOfDay AddSeconds(int seconds)
        {
            long newValue = Value + (long)seconds * 1000;

            if (newValue < 0 || newValue >= maxvalue)
            {
                throw new OverflowException("Adding seconds causes overflow.");
            }

            return new TimeOfDay((int)newValue);
        }
        public override bool Equals(object obj)
        {
            if (obj is TimeOfDay other)
            {
                return Value == other.Value;
            }
            return false;
        }
        public override int GetHashCode() => Value.GetHashCode();
        public static TimeOfDay Parse(string time) => new TimeOfDay(ParseString(time));
        private static int ParseString(string time)
        {
            if (time.Contains("end", StringComparison.OrdinalIgnoreCase))
            {
                return maxvalue;
            }
            if (byte.TryParse(time, out var hours))
            {
                if (hours > 23) throw new Exception("Overflow");
                return hours * 3600 * 1000;
            }
            if (DateTime.TryParse(time, out var dt))
            {
                return Convert.ToInt32(dt.TimeOfDay.TotalMilliseconds);
            }
            throw new FormatException("Invalid time format.");
        }
        public XmlSchema GetSchema() => null;
        public void ReadXml(XmlReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));

            reader.ReadStartElement();

            reader.ReadStartElement("Value");
            _value = ParseString(reader.ReadString());
            reader.ReadEndElement();

            reader.ReadEndElement();
        }
        public void WriteXml(XmlWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            writer.WriteStartElement("Value");
            writer.WriteString(ToString());
            writer.WriteEndElement();
        }
    }


    public class TimeRange : IXmlSerializable
    {
        private TimeOfDay _starttime;
        public TimeOfDay StartTime => _starttime;
        private TimeOfDay _endtime;
        public TimeOfDay EndTime => _endtime;
        public static bool operator ==(TimeRange left, TimeRange right) => left.StartTime == right.StartTime && left.EndTime == right.EndTime;
        public static bool operator !=(TimeRange left, TimeRange right) => left.StartTime != right.StartTime || left.EndTime != right.EndTime;
        public override bool Equals(object obj)
        {
            if (obj is TimeRange other)
            {
                return this == other;
            }
            return false;
        }
        public override int GetHashCode()
        {
            unchecked // Overflow is fine, just wrap
            {
                int hash = 17;
                hash = hash * 23 + StartTime.GetHashCode();
                hash = hash * 23 + EndTime.GetHashCode();
                return hash;
            }
        }
        public TimeRange(TimeOfDay startTime, TimeOfDay endTime)
        {
            if (startTime > endTime)
            {
                throw new ArgumentException("End time must be after start time.");
            }

            _starttime = startTime;
            _endtime = endTime;
        }
        public TimeRange()
        {
            _starttime = TimeOfDay.Midnight;
            _endtime = TimeOfDay.EndOfDay;
        }
        public bool Contains(TimeOfDay time) => time >= StartTime && time <= EndTime;
        public bool Contains(DateTime dateTime)
        {
            var timeOfDay = dateTime.TimeOfDay;
            var timeToCheck = new TimeOfDay((byte)timeOfDay.Hours, (byte)timeOfDay.Minutes, (byte)timeOfDay.Seconds, false);
            return Contains(timeToCheck);
        }
        public static TimeRange Parse(string timeRange)
        {
            if (TryParse(timeRange, out var tr))
                return tr;
            throw new ArgumentException("Invalid TimeRange");

            //if (string.IsNullOrWhiteSpace(timeRange)) return null;
            //if (timeRange.Contains("all day", StringComparison.OrdinalIgnoreCase)) return EntireDay;
            //string[] parts = timeRange.Split('-');

            //if (parts.Length != 2)
            //{
            //    return null;
            //}

            //TimeOfDay startTime = TimeOfDay.Parse(parts[0].Trim());
            //TimeOfDay endTime = TimeOfDay.Parse(parts[1].Trim());

            //if (startTime > endTime && endTime.Hours < 12) endTime = endTime.AddHours(12);

            //return new TimeRange(startTime, endTime);
        }
        public static bool TryParse(string timeRange, out TimeRange parsed)
        {
            parsed = null;
            if (string.IsNullOrWhiteSpace(timeRange)) return false;
            if (timeRange.Contains("all day", StringComparison.OrdinalIgnoreCase))
            {
                parsed = EntireDay;
                return true;
            }
            string[] parts = timeRange.Split('-');

            if (parts.Length != 2)
            {
                return false;
            }

            TimeOfDay startTime = TimeOfDay.Parse(parts[0].Trim());
            TimeOfDay endTime = TimeOfDay.Parse(parts[1].Trim());

            if (startTime > endTime && endTime.Hours < 12) endTime = endTime.AddHours(12);
            try
            {
                parsed = new TimeRange(startTime, endTime);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        public static TimeRange Default = new TimeRange();
        public TimeSpan Duration()
        {
            return TimeSpan.FromMilliseconds(EndTime.Value - StartTime.Value);
        }
        public static TimeRange EntireDay => new TimeRange(TimeOfDay.Midnight, TimeOfDay.EndOfDay);
        public override string ToString()
        {
            if (StartTime == TimeOfDay.Midnight && EndTime.IsEndOfDay)
            {
                return "All day";
            }
            else
            {
                return $"{StartTime} - {EndTime}";
            }
        }
        public XmlSchema GetSchema() => null;
        public void ReadXml(XmlReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));

            reader.ReadStartElement();

            reader.ReadStartElement("StartTime");
            _starttime = TimeOfDay.Parse(reader.ReadString());
            reader.ReadEndElement();

            reader.ReadStartElement("EndTime");
            _endtime = TimeOfDay.Parse(reader.ReadString());
            reader.ReadEndElement();

            reader.ReadEndElement();
        }
        public void WriteXml(XmlWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            writer.WriteStartElement("StartTime");
            writer.WriteString(StartTime.ToString());
            writer.WriteEndElement();
            writer.WriteStartElement("EndTime");
            writer.WriteString(EndTime.ToString());
            writer.WriteEndElement();
        }
    }
}
