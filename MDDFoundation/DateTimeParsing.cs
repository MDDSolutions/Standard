using System;
using System.Collections.Generic;
namespace MDDFoundation;
public partial class Foundation
{
    public static DateTime? ExtractDateTimeFromString(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var chunks = SplitAlphaNumChunks(input);

        // Scan windows over *adjacent* chunks.
        // Prefer more specific matches: datetime > date.
        for (int i = 0; i < chunks.Count; i++)
        {
            // YYYY MM DD HH MM SS (all numeric, adjacent)
            if (TryParse_YMD_HMS(chunks, i, out DateTime dt6))
                return dt6;

            // YYYY MM DD HH MM
            if (TryParse_YMD_HM(chunks, i, out DateTime dt5))
                return dt5;

            // YYYY MM DD
            if (TryParse_YMD(chunks, i, out DateTime dt3))
                return dt3;

            // YYYY Mon DD [HH MM SS]  (Mon is text month)
            if (TryParse_Y_Mon_D(chunks, i, out DateTime dtText1))
                return dtText1;

            // DD Mon YYYY
            if (TryParse_D_Mon_Y(chunks, i, out DateTime dtText2))
                return dtText2;

            // Mon DD YYYY
            if (TryParse_Mon_D_Y(chunks, i, out DateTime dtText3))
                return dtText3;

            // Ambiguous numeric formats: only accept when unambiguous
            // DD MM YYYY or MM DD YYYY
            if (TryParse_DMY_or_MDY_Cautious(chunks, i, out DateTime dtAmbig))
                return dtAmbig;

            // Compact YYYYMMDD as a single chunk
            if (TryParse_YYYYMMDD_Chunk(chunks, i, out DateTime dtCompact))
                return dtCompact;
        }

        return null;
    }

    // ---------- patterns (adjacent windows over chunks[]) ----------

    private static bool TryParse_YMD(List<string> c, int i, out DateTime dt)
    {
        dt = default;
        if (i + 2 >= c.Count) return false;

        if (!IsYear4(c[i], out int y)) return false;
        if (!TryInt(c[i + 1], out int m)) return false;
        if (!TryInt(c[i + 2], out int d)) return false;

        if (!IsValidDate(y, m, d)) return false;
        dt = new DateTime(y, m, d);
        return true;
    }

    private static bool TryParse_YMD_HM(List<string> c, int i, out DateTime dt)
    {
        dt = default;
        if (i + 4 >= c.Count) return false;

        if (!TryParse_YMD(c, i, out DateTime date)) return false;
        if (!TryInt(c[i + 3], out int hh) || hh < 0 || hh > 23) return false;
        if (!TryInt(c[i + 4], out int mm) || mm < 0 || mm > 59) return false;

        dt = new DateTime(date.Year, date.Month, date.Day, hh, mm, 0);
        return true;
    }

    private static bool TryParse_YMD_HMS(List<string> c, int i, out DateTime dt)
    {
        dt = default;
        if (i + 5 >= c.Count) return false;

        if (!TryParse_YMD(c, i, out DateTime date)) return false;
        if (!TryInt(c[i + 3], out int hh) || hh < 0 || hh > 23) return false;
        if (!TryInt(c[i + 4], out int mm) || mm < 0 || mm > 59) return false;
        if (!TryInt(c[i + 5], out int ss) || ss < 0 || ss > 59) return false;

        dt = new DateTime(date.Year, date.Month, date.Day, hh, mm, ss);
        return true;
    }

    // YYYY Mon DD [HH MM SS]
    private static bool TryParse_Y_Mon_D(List<string> c, int i, out DateTime dt)
    {
        dt = default;
        if (i + 2 >= c.Count) return false;

        if (!IsYear4(c[i], out int y)) return false;
        if (!TryParseMonthName(c[i + 1], out int m)) return false;
        if (!TryInt(c[i + 2], out int d)) return false;

        if (!IsValidDate(y, m, d)) return false;

        // Optional time right after
        if (i + 5 < c.Count &&
            TryInt(c[i + 3], out int hh) && hh >= 0 && hh <= 23 &&
            TryInt(c[i + 4], out int mm) && mm >= 0 && mm <= 59 &&
            TryInt(c[i + 5], out int ss) && ss >= 0 && ss <= 59)
        {
            dt = new DateTime(y, m, d, hh, mm, ss);
            return true;
        }

        dt = new DateTime(y, m, d);
        return true;
    }

    // DD Mon YYYY
    private static bool TryParse_D_Mon_Y(List<string> c, int i, out DateTime dt)
    {
        dt = default;
        if (i + 2 >= c.Count) return false;

        if (!TryInt(c[i], out int d)) return false;
        if (!TryParseMonthName(c[i + 1], out int m)) return false;
        if (!IsYear4(c[i + 2], out int y)) return false;

        if (!IsValidDate(y, m, d)) return false;
        dt = new DateTime(y, m, d);
        return true;
    }

    // Mon DD YYYY
    private static bool TryParse_Mon_D_Y(List<string> c, int i, out DateTime dt)
    {
        dt = default;
        if (i + 2 >= c.Count) return false;

        if (!TryParseMonthName(c[i], out int m)) return false;
        if (!TryInt(c[i + 1], out int d)) return false;
        if (!IsYear4(c[i + 2], out int y)) return false;

        if (!IsValidDate(y, m, d)) return false;
        dt = new DateTime(y, m, d);
        return true;
    }

    // Cautious numeric: accept only if unambiguous.
    // Patterns: DD MM YYYY or MM DD YYYY (adjacent)
    private static bool TryParse_DMY_or_MDY_Cautious(List<string> c, int i, out DateTime dt)
    {
        dt = default;
        if (i + 2 >= c.Count) return false;

        if (!TryInt(c[i], out int a)) return false;
        if (!TryInt(c[i + 1], out int b)) return false;
        if (!IsYear4(c[i + 2], out int y)) return false;

        // Unambiguous if one side can't be a month ( > 12 )
        bool aCouldBeMonth = a >= 1 && a <= 12;
        bool bCouldBeMonth = b >= 1 && b <= 12;

        // If both could be months, it's ambiguous: reject.
        if (aCouldBeMonth && bCouldBeMonth)
            return false;

        // If a can't be month but b can: a must be day, b month => DMY
        if (!aCouldBeMonth && bCouldBeMonth)
        {
            int d = a, m = b;
            if (!IsValidDate(y, m, d)) return false;
            dt = new DateTime(y, m, d);
            return true;
        }

        // If b can't be month but a can: b must be day, a month => MDY
        if (!bCouldBeMonth && aCouldBeMonth)
        {
            int m = a, d = b;
            if (!IsValidDate(y, m, d)) return false;
            dt = new DateTime(y, m, d);
            return true;
        }

        // If neither could be month, reject (both > 12)
        return false;
    }

    private static bool TryParse_YYYYMMDD_Chunk(List<string> c, int i, out DateTime dt)
    {
        dt = default;
        string s = c[i];
        if (s == null || s.Length != 8) return false;
        if (!IsAllDigits(s)) return false;

        if (!TryInt(s.Substring(0, 4), out int y)) return false;
        if (!TryInt(s.Substring(4, 2), out int m)) return false;
        if (!TryInt(s.Substring(6, 2), out int d)) return false;

        if (!IsValidDate(y, m, d)) return false;
        dt = new DateTime(y, m, d);
        return true;
    }

    // ---------- tokenization & helpers ----------

    private static List<string> SplitAlphaNumChunks(string s)
    {
        var list = new List<string>();
        int i = 0;

        while (i < s.Length)
        {
            while (i < s.Length && !char.IsLetterOrDigit(s[i])) i++;
            if (i >= s.Length) break;

            int start = i;
            i++;
            while (i < s.Length && char.IsLetterOrDigit(s[i])) i++;

            list.Add(s.Substring(start, i - start));
        }

        return list;
    }

    private static bool IsYear4(string s, out int year)
    {
        year = 0;
        if (s == null || s.Length != 4) return false;
        if (!TryInt(s, out year)) return false;
        return year >= 1900 && year <= 2100;
    }

    private static bool IsValidDate(int y, int m, int d)
    {
        if (y < 1900 || y > 2100) return false;
        if (m < 1 || m > 12) return false;
        if (d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        return true;
    }

    private static bool TryInt(string s, out int value)
    {
        return int.TryParse(s, out value);
    }

    private static bool IsAllDigits(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        for (int i = 0; i < s.Length; i++)
            if (!char.IsDigit(s[i])) return false;
        return true;
    }

    // Month names: "March", "mar", "apr", etc. Case-insensitive.
    private static bool TryParseMonthName(string token, out int month)
    {
        month = 0;
        if (string.IsNullOrWhiteSpace(token)) return false;

        string t = token.Trim().ToLowerInvariant();
        if (t.Length >= 3) t = t.Substring(0, 3);

        switch (t)
        {
            case "jan": month = 1; return true;
            case "feb": month = 2; return true;
            case "mar": month = 3; return true;
            case "apr": month = 4; return true;
            case "may": month = 5; return true;
            case "jun": month = 6; return true;
            case "jul": month = 7; return true;
            case "aug": month = 8; return true;
            case "sep": month = 9; return true;
            case "oct": month = 10; return true;
            case "nov": month = 11; return true;
            case "dec": month = 12; return true;
            default: return false;
        }
    }
}
