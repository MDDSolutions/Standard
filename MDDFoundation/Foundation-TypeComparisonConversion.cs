using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MDDFoundation
{
    public static partial class Foundation
    {
        public static bool ValueEquals(object val1, object val2)
        {
            // covers (null, null) and same reference fast-path
            if (ReferenceEquals(val1, val2)) return true;

            // at this point, at least one is non-null; if either is null → not equal
            if (val1 is null || val2 is null) return false;

            if (val1 is ValueType && val2 is ValueType)
                return val1.Equals(val2);

            if (val1 is string s1 && val2 is string s2)
                return string.Equals(s1, s2, StringComparison.Ordinal);

            if (val1 is byte[] b1 && val2 is byte[] b2)
                return b1.SequenceEqual(b2);

            if (val1 is Array arr1 && val2 is Array arr2)
            {
                var t = arr1.GetType().GetElementType();
                if (t != null && t.IsPrimitive)
                {
                    if (arr1.Length != arr2.Length) return false;
                    for (int i = 0; i < arr1.Length; i++)
                        if (!Equals(arr1.GetValue(i), arr2.GetValue(i)))
                            return false;
                    return true;
                }
                return arr1.Cast<object>().SequenceEqual(arr2.Cast<object>());
            }

            return val1.Equals(val2);
        }
        public static bool ValueEquals(object a, object b, Type declaredType)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (declaredType != null)
            {
                var t = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
                a = Coerce(a, t);
                b = Coerce(b, t);
            }
            return ValueEquals(a, b);
        }
        public static bool TryParseDateTime(string dateString, out DateTime dt)
        {
            dateString = dateString.Trim();
            dateString = dateString.Trim('•');
            dateString = dateString.Replace("Released:", "");
            dateString = dateString.Trim();

            if (dateString.Length > 20 && dateString.Contains("(") && dateString.Contains(")"))
            {
                dateString = TextBetween(dateString, "(", ")");
            }


            // First, try to parse the date using the regular DateTime.TryParse
            if (DateTime.TryParse(dateString, out dt))
            {
                if (dateString.Contains(dt.Day.ToString()))
                    return true;
            }
            if (dateString.Length > 9 && dateString.Substring(9, 1) == ":")
            {
                if (DateTime.TryParse($"{dateString.Substring(0, 6)}, {DateTime.Now.Year}", out dt))
                {
                    if (TimeSpan.TryParse(dateString.Substring(7), out TimeSpan ts))
                    {
                        dt = dt + ts;
                    }
                    return true;
                }
            }

            if (dateString.Equals("last week", StringComparison.OrdinalIgnoreCase))
            {
                dt = DateTime.Now.Date.AddDays(-7);
                return true;
            }

            if (dateString.Equals("today", StringComparison.OrdinalIgnoreCase))
            {
                dt = DateTime.Now.Date;
                return true;
            }

            var match = Regex.Match(dateString, @"(\d+)\s+(day|week|month|year)s?\s+ago", RegexOptions.IgnoreCase);

            if (match.Success)
            {
                int value = int.Parse(match.Groups[1].Value);
                string unit = match.Groups[2].Value.ToLower();

                switch (unit)
                {
                    case "day":
                        dt = DateTime.Now.Date.AddDays(-value);
                        return true;
                    case "week":
                        dt = DateTime.Now.Date.AddDays(-7 * value);
                        return true;
                    case "month":
                        // Approximate as 30 days per month
                        dt = DateTime.Now.Date.AddDays(-30 * value);
                        return true;
                    case "year":
                        dt = DateTime.Now.Date.AddYears(-value);
                        return true;
                }
            }



            // If regular parsing fails, remove the suffix from the day part
            string cleanedDateString = Regex.Replace(dateString, @"\b(\d{1,2})(st|nd|rd|th)\b", "$1");


            //try a general parse on the cleaned string
            if (DateTime.TryParse(cleanedDateString, out dt))
            {
                //if (dateString.Contains(dt.Day.ToString()))
                return true;
            }

            // Define the format of the cleaned date string
            string format = "d MMM yyyy";
            CultureInfo provider = CultureInfo.InvariantCulture;

            // Try to parse the cleaned date string
            if (DateTime.TryParseExact(cleanedDateString, format, provider, DateTimeStyles.None, out dt))
                return true;

            return false;
        }
        public static double Subtract(this Point pnt1, Point pnt2)
        {
            return Math.Sqrt((pnt1.X - pnt2.X) * (pnt1.X - pnt2.X) + (pnt1.Y - pnt2.Y) * (pnt1.Y - pnt2.Y));
        }
        public static string TextBetween(string SearchString, string BeforeStr, string AfterStr)
        {
            if (String.IsNullOrWhiteSpace(SearchString))
                return null;
            int TmpIndex = SearchString.IndexOf(BeforeStr, StringComparison.OrdinalIgnoreCase);
            if (TmpIndex == -1)
                return null;
            else
            {
                TmpIndex = TmpIndex + BeforeStr.Length;
                int AfterIndex = SearchString.IndexOf(AfterStr, TmpIndex, StringComparison.OrdinalIgnoreCase);
                if (AfterIndex == -1)
                    return SearchString.Substring(TmpIndex);
                else
                    return SearchString.Substring(TmpIndex, AfterIndex - TmpIndex);
            }
        }
        public static string Replace(this string str, string oldValue, string newValue, StringComparison comparison)
        {
            StringBuilder sb = new StringBuilder();

            int previousIndex = 0;
            int index = str.IndexOf(oldValue, comparison);
            while (index != -1)
            {
                sb.Append(str.Substring(previousIndex, index - previousIndex));
                sb.Append(newValue);
                index += oldValue.Length;

                previousIndex = index;
                index = str.IndexOf(oldValue, index, comparison);
            }
            sb.Append(str.Substring(previousIndex));

            return sb.ToString();
        }
        public static bool Contains(this string source, string toCheck, StringComparison comp)
        {
            return source?.IndexOf(toCheck, comp) >= 0;
        }
        public static bool IsSameAs<T>(this T ref1, T ref2)
        {
            foreach (var prop in typeof(T).GetProperties())
                if (!StructuralComparisons.StructuralEqualityComparer.Equals(prop.GetValue(ref2), prop.GetValue(ref1)))
                    return false;
            return true;
        }
        public static object Coerce(object v, Type target, IFormatProvider provider = null)
        {
            provider = provider ?? CultureInfo.InvariantCulture;
            if (v is null) return null;

            // unwrap nullable
            var t = Nullable.GetUnderlyingType(target) ?? target;

            // already the right type
            if (t.IsInstanceOfType(v)) return v;

            // normalize strings
            if (v is string s) v = s.Trim();

            // empty string → null for nullable targets
            if (v is string s2 && s2.Length == 0 && Nullable.GetUnderlyingType(target) != null)
                return null;

            // enums: string name or underlying number
            if (t.IsEnum)
            {
                if (v is string es) return Enum.Parse(t, es, ignoreCase: true);
                var ui = Convert.ChangeType(v, Enum.GetUnderlyingType(t), provider);
                return Enum.ToObject(t, ui);
            }

            if (t == typeof(Guid)) return v is Guid g ? g : Guid.Parse(Convert.ToString(v, provider));
            if (t == typeof(DateTime)) return v is DateTime dt ? dt : DateTime.Parse(Convert.ToString(v, provider), provider, DateTimeStyles.None);
            if (t == typeof(DateTimeOffset)) return v is DateTimeOffset dto ? dto : DateTimeOffset.Parse(Convert.ToString(v, provider), provider, DateTimeStyles.None);
            if (t == typeof(TimeSpan)) return v is TimeSpan ts ? ts : TimeSpan.Parse(Convert.ToString(v, provider), provider);

            // numeric / bool / char and everything IConvertible
            return Convert.ChangeType(v, t, provider);
        }
        public static bool TryCoerce(object v, Type target, out object result, IFormatProvider provider = null)
        {
            try { result = Coerce(v, target, provider); return true; }
            catch { result = null; return false; }
        }
        // (optional) from string with empty→null behavior for nullable
        public static bool TryCoerceFromString(string text, Type target, out object result, IFormatProvider provider = null)
        {
            provider = provider ?? CultureInfo.InvariantCulture;
            if (text == null) { result = null; return true; }

            var s = text.Trim();
            if (s.Length == 0 && Nullable.GetUnderlyingType(target) != null) { result = null; return true; }

            try { result = Coerce(s, target, provider); return true; }
            catch { result = null; return false; }
        }
        public static bool IsDefaultOrNull(object key)
        {
            if (key == null)
                return true;

            var type = key.GetType();

            // Int32: treat 0 as default
            if (type == typeof(int))
                return (int)key == 0;

            // String: treat null or empty as default
            if (type == typeof(string))
                return string.IsNullOrEmpty((string)key);

            // Guid: treat Guid.Empty as default
            if (type == typeof(Guid))
                return (Guid)key == Guid.Empty;

            // Long: treat 0 as default
            if (type == typeof(long))
                return (long)key == 0L;

            // Add more types as needed...

            // For other value types, compare to Activator.CreateInstance(type)
            if (type.IsValueType)
                return key.Equals(Activator.CreateInstance(type));

            // For all other cases, just check for null
            return false;
        }
        public static bool TryConvert(string text, Type target, out object value, out string error)
        {
            value = null; error = null;
            var t = Under(target);

            if (string.IsNullOrWhiteSpace(text) && (t == typeof(string) || Nullable.GetUnderlyingType(target) != null))
            {
                value = null;
                return true;
            }

            if (t == typeof(string)) { value = text; return true; }

            if (t == typeof(DateTime))
            {
                DateTime dt;

                if (Foundation.TryParseDateTime(text, out dt))
                { value = dt; return true; }
                error = "Enter valid datetime"; return false;
            }

            int i; long l; float f; double d; decimal m;
            bool b;
            if (t == typeof(int)) { if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) { value = i; return true; } error = "Invalid integer"; return false; }
            if (t == typeof(long)) { if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) { value = l; return true; } error = "Invalid integer"; return false; }
            if (t == typeof(float)) { if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) { value = f; return true; } error = "Invalid number"; return false; }
            if (t == typeof(double)) { if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { value = d; return true; } error = "Invalid number"; return false; }
            if (t == typeof(decimal)) { if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out m)) { value = m; return true; } error = "Invalid number"; return false; }
            if (t == typeof(bool)) { if (bool.TryParse(text, out b)) { value = b; return true; } error = "True/False"; return false; }

            if (t.IsEnum)
            {
                try { value = Enum.Parse(t, text, true); return true; }
                catch { error = "Invalid option"; return false; }
            }

            try { value = Convert.ChangeType(text, t, CultureInfo.InvariantCulture); return true; }
            catch { error = "Invalid value"; return false; }
        }
        public static bool IsDateTimeType(Type t) => (Nullable.GetUnderlyingType(t) ?? t) == typeof(DateTime);
        public static Type Under(Type t) { return Nullable.GetUnderlyingType(t) ?? t; }


        /// <summary>
        /// Deprecated
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <param name="compareValue"></param>
        /// <returns></returns>
        public static T NullIf<T>(this T value, T compareValue) where T : class
        {
            return value == compareValue ? null : value;
        }
        /// <summary>
        /// Deprecated
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static string NullIf(this string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
