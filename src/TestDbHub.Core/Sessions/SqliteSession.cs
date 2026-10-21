using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TestDbHub.Core.Sessions;

internal sealed partial class SqliteSession(SqliteConnection conn) : ISqlSession
{
    public async Task<RawResult> RunAsync(string sql, int maxRows = int.MaxValue, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (reader.FieldCount == 0)
                return new RawResult(null, [], 0, Math.Max(reader.RecordsAffected, 0));

            var rows = new List<object?[]>();
            long count = 0;
            string?[] declared = new string?[reader.FieldCount];
            bool first = true;
            while (await reader.ReadAsync(ct))
            {
                if (first)
                {
                    for (int i = 0; i < declared.Length; i++) declared[i] = DeclaredType(reader, i);
                    first = false;
                }
                if (count++ < maxRows)
                {
                    var row = new object?[reader.FieldCount];
                    for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }
            }
            if (first)
            {
                for (int i = 0; i < declared.Length; i++) declared[i] = DeclaredType(reader, i);
            }

            var columns = new List<PortableColumn>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var col = MapDeclared(reader.GetName(i), declared[i], rows.Select(r => r[i]).FirstOrDefault(v => v is not null));
                columns.Add(col);
                if (col.Type == PortableType.Decimal) NormalizeDecimals(rows, i);
            }
            return new RawResult(columns, rows, count, 0);
        }
        catch (SqliteException ex)
        {
            throw new SqlExecutionException(ex.Message);
        }
    }

    private static string? DeclaredType(SqliteDataReader reader, int i)
    {
        try
        {
            return reader.GetDataTypeName(i);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\(\s*(\d+)\s*(?:,\s*(\d+)\s*)?\)")]
    private static partial Regex PrecisionRegex();

    /// <summary>Map a SQLite declared column type (or, for expressions, a sample value) to a portable type.</summary>
    internal static PortableColumn MapDeclared(string name, string? declared, object? sample)
    {
        var d = (declared ?? "").ToUpperInvariant();
        if (d.Contains("BOOL")) return new(name, PortableType.Boolean);
        if (d.Contains("BIGINT") || d == "INTEGER") return new(name, PortableType.BigInt);
        if (d.Contains("INT")) return new(name, PortableType.Int);
        if (d.StartsWith("DEC", StringComparison.Ordinal) || d.StartsWith("NUMERIC", StringComparison.Ordinal))
        {
            var m = PrecisionRegex().Match(d);
            int? precision = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
            int? scale = m.Success ? (m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0) : null;
            return new(name, PortableType.Decimal, precision, scale);
        }
        if (d.Contains("DOUB") || d.Contains("REAL") || d.Contains("FLOA")) return new(name, PortableType.Double);
        if (d.Contains("DATETIME") || d.Contains("TIMESTAMP")) return new(name, PortableType.DateTime);
        if (d.Contains("DATE")) return new(name, PortableType.Date);
        if (d.Contains("TIME")) return new(name, PortableType.Time);
        if (d.Contains("JSON")) return new(name, PortableType.Json);
        if (d.Contains("BLOB") || d.Contains("BINARY")) return new(name, PortableType.Binary);
        if (d.Contains("CHAR") || d.Contains("TEXT") || d.Contains("CLOB")) return new(name, PortableType.Text);
        return sample switch
        {
            long => new(name, PortableType.BigInt),
            double => new(name, PortableType.Double),
            byte[] => new(name, PortableType.Binary),
            _ => new(name, PortableType.Text),
        };
    }

    // SQLite stores DECIMAL values as INTEGER or REAL; surface them as decimals.
    private static void NormalizeDecimals(List<object?[]> rows, int i)
    {
        foreach (var row in rows)
        {
            row[i] = row[i] switch
            {
                long l => (decimal)l,
                double d when double.IsFinite(d) && Math.Abs(d) < 7.9e28 => (decimal)d,
                string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var m) => m,
                var v => v,
            };
        }
    }

    public ValueTask DisposeAsync() => conn.DisposeAsync();
}
