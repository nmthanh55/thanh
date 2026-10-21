using System.Globalization;
using System.Text;

namespace TestDbHub.Core;

public enum SqlDialect { MySql, Helios }

/// <summary>
/// Column types and literals that both MySQL and HeliosDB-Nano understand,
/// used to recreate a table on another engine and to write .sql exports.
/// </summary>
public static class PortableSql
{
    public static string ColumnType(PortableColumn col, SqlDialect dialect)
    {
        bool mysql = dialect == SqlDialect.MySql;
        return col.Type switch
        {
            PortableType.Boolean => "BOOLEAN",
            PortableType.Int => "INT",
            PortableType.BigInt => "BIGINT",
            PortableType.Double => "DOUBLE PRECISION",
            PortableType.Decimal => DecimalType(col),
            PortableType.Date => "DATE",
            PortableType.DateTime => mysql ? "DATETIME(6)" : "TIMESTAMP",
            PortableType.Time => "TIME",
            PortableType.Json => "JSON",
            PortableType.Uuid => mysql ? "CHAR(36)" : "TEXT",
            PortableType.Binary => mysql ? "LONGBLOB" : "BYTEA",
            _ => mysql ? "LONGTEXT" : "TEXT",
        };
    }

    private static string DecimalType(PortableColumn col)
    {
        int scale = Math.Clamp(col.Scale ?? 10, 0, 30);
        int precision = Math.Clamp(col.Precision ?? 38, Math.Max(scale, 1), 65);
        return $"DECIMAL({precision},{scale})";
    }

    /// <summary>
    /// Fill in DECIMAL precision/scale from the data when the server did not
    /// report usable values (HeliosDB sends none).
    /// </summary>
    public static IReadOnlyList<PortableColumn> InferDecimals(IReadOnlyList<PortableColumn> columns, IReadOnlyList<object?[]> rows)
    {
        var result = columns.ToList();
        for (int i = 0; i < result.Count; i++)
        {
            var col = result[i];
            if (col.Type != PortableType.Decimal || col.Precision is >= 1 and <= 65 && col.Scale is >= 0 and <= 30) continue;
            int scale = 0, intDigits = 1;
            bool any = false;
            foreach (var row in rows)
            {
                if (i >= row.Length || row[i] is not decimal d) continue;
                any = true;
                scale = Math.Max(scale, d.Scale);
                var whole = Math.Truncate(Math.Abs(d)).ToString(CultureInfo.InvariantCulture);
                intDigits = Math.Max(intDigits, whole.Length);
            }
            result[i] = any
                ? col with { Precision = Math.Min(Math.Max(intDigits + scale, 18), 65), Scale = Math.Min(scale, 30) }
                : col with { Precision = 38, Scale = 10 };
        }
        return result;
    }

    public static string Literal(object? value, PortableColumn? col, SqlDialect dialect)
    {
        bool mysql = dialect == SqlDialect.MySql;
        switch (value)
        {
            case null:
            case DBNull:
                return "NULL";
            case bool b:
                return mysql ? (b ? "1" : "0") : (b ? "TRUE" : "FALSE");
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;
            case decimal d:
                return d.ToString(CultureInfo.InvariantCulture);
            case float f:
                return float.IsFinite(f) ? f.ToString("R", CultureInfo.InvariantCulture) : "NULL";
            case double db:
                return double.IsFinite(db) ? db.ToString("R", CultureInfo.InvariantCulture) : "NULL";
            case DateTime dt:
                return Quote(col?.Type == PortableType.Date ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture), dialect);
            case DateTimeOffset dto:
                return Quote(dto.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture), dialect);
            case DateOnly date:
                return Quote(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), dialect);
            case TimeSpan ts:
                return Quote(FormatTime(ts), dialect);
            case byte[] bytes:
                return mysql ? $"X'{Convert.ToHexString(bytes)}'" : $"'\\x{Convert.ToHexString(bytes)}'";
            default:
                return Quote(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", dialect);
        }
    }

    public static string Quote(string s, SqlDialect dialect)
    {
        var sb = new StringBuilder(s.Length + 2).Append('\'');
        foreach (char c in s)
        {
            if (c == '\'') sb.Append("''");
            // MySQL treats backslash as an escape character by default.
            else if (c == '\\' && dialect == SqlDialect.MySql) sb.Append(@"\\");
            else sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    private static string FormatTime(TimeSpan ts)
    {
        string sign = ts < TimeSpan.Zero ? "-" : "";
        ts = ts.Duration();
        string s = $"{sign}{(long)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
        long micro = ts.Ticks % TimeSpan.TicksPerSecond / 10;
        return micro == 0 ? s : $"{s}.{micro:000000}";
    }
}
