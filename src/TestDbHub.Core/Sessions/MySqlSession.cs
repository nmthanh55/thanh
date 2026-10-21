using MySqlConnector;

namespace TestDbHub.Core.Sessions;

internal sealed class MySqlSession(MySqlConnection conn) : ISqlSession
{
    public MySqlConnection Connection => conn;

    public async Task<RawResult> RunAsync(string sql, int maxRows = int.MaxValue, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = new MySqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (reader.FieldCount == 0)
                return new RawResult(null, [], 0, Math.Max(reader.RecordsAffected, 0));

            var columns = reader.GetColumnSchema().Select(c => new PortableColumn(
                c.ColumnName,
                MapType(c is MySqlDbColumn m ? m.ProviderType : MySqlDbType.VarString),
                c.NumericPrecision,
                c.NumericScale)).ToList();
            var rows = new List<object?[]>();
            long count = 0;
            while (await reader.ReadAsync(ct))
            {
                if (count++ < maxRows) rows.Add(ReadRow(reader));
            }
            return new RawResult(columns, rows, count, 0);
        }
        catch (MySqlException ex)
        {
            throw new SqlExecutionException(ex.Message);
        }
    }

    private static object?[] ReadRow(MySqlDataReader reader)
    {
        var row = new object?[reader.FieldCount];
        for (int i = 0; i < row.Length; i++)
        {
            try
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or MySqlConversionException)
            {
                // Values the driver cannot convert (e.g. invalid dates) are kept as text.
                row[i] = reader.GetString(i);
            }
        }
        return row;
    }

    internal static PortableType MapType(MySqlDbType type) => type switch
    {
        MySqlDbType.Bool => PortableType.Boolean,
        MySqlDbType.Byte or MySqlDbType.UByte or MySqlDbType.Int16 or MySqlDbType.UInt16
            or MySqlDbType.Int24 or MySqlDbType.UInt24 or MySqlDbType.Int32 or MySqlDbType.Year => PortableType.Int,
        MySqlDbType.UInt32 or MySqlDbType.Int64 or MySqlDbType.Bit => PortableType.BigInt,
        MySqlDbType.UInt64 or MySqlDbType.Decimal or MySqlDbType.NewDecimal => PortableType.Decimal,
        MySqlDbType.Float or MySqlDbType.Double => PortableType.Double,
        MySqlDbType.Date or MySqlDbType.Newdate => PortableType.Date,
        MySqlDbType.DateTime or MySqlDbType.Timestamp => PortableType.DateTime,
        MySqlDbType.Time => PortableType.Time,
        MySqlDbType.JSON => PortableType.Json,
        MySqlDbType.Guid => PortableType.Uuid,
        MySqlDbType.Binary or MySqlDbType.VarBinary or MySqlDbType.TinyBlob or MySqlDbType.MediumBlob
            or MySqlDbType.Blob or MySqlDbType.LongBlob => PortableType.Binary,
        _ => PortableType.Text,
    };

    public ValueTask DisposeAsync() => conn.DisposeAsync();
}
