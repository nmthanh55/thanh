using System.Data;
using System.Globalization;
using TestDbHub.Core;
using TestDbHub.Core.Engines;

namespace TestDbHub.Wpf.ViewModels;

/// <summary>One tab in the results area.</summary>
public sealed class ResultViewModel
{
    public required string Header { get; init; }
    public required string Sql { get; init; }
    public required string Summary { get; init; }
    public DataView? Grid { get; init; }
    public string? Message { get; init; }
    public bool IsError { get; init; }
    public bool HasGrid => Grid is not null;
    public bool HasMessage => Grid is null;

    public static ResultViewModel From(QueryResult r, int index)
    {
        string elapsed = $"{r.Elapsed.TotalMilliseconds:0} ms";
        if (r.IsError)
            return new() { Header = $"#{index} Lỗi", Sql = r.Sql, Summary = $"Lỗi · {elapsed}", Message = r.Error, IsError = true };
        if (!r.HasRows)
            return new() { Header = $"#{index}", Sql = r.Sql, Summary = $"{r.AffectedRows} dòng bị ảnh hưởng · {elapsed}", Message = "Thực thi thành công." };
        string rows = r.Truncated ? $"{r.RowCount} dòng (hiển thị {DbEngine.MaxRows} dòng đầu)" : $"{r.RowCount} dòng";
        return new() { Header = $"#{index} ({r.RowCount})", Sql = r.Sql, Summary = $"{rows} · {elapsed}", Grid = BuildTable(r.Columns!, r.Rows!).DefaultView };
    }

    public static ResultViewModel FromTable(string title, IReadOnlyList<string> columns, IEnumerable<object?[]> rows)
    {
        var list = rows.ToList();
        return new() { Header = title, Sql = title, Summary = $"{list.Count} dòng", Grid = BuildTable(columns, list).DefaultView };
    }

    // Columns are named c0, c1... (captions hold the real names) so duplicate
    // or unusual column names never break the DataGrid bindings.
    private static DataTable BuildTable(IReadOnlyList<string> columns, IEnumerable<object?[]> rows)
    {
        var table = new DataTable();
        for (int i = 0; i < columns.Count; i++) table.Columns.Add(new DataColumn($"c{i}", typeof(string)) { Caption = columns[i] });
        foreach (var row in rows)
        {
            var values = new object[columns.Count];
            for (int i = 0; i < values.Length; i++) values[i] = Format(i < row.Length ? row[i] : null);
            table.Rows.Add(values);
        }
        return table;
    }

    public static string Format(object? v) => v switch
    {
        null or DBNull => "NULL",
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
        byte[] b => b.Length > 64 ? $"0x{Convert.ToHexString(b, 0, 64)}… ({b.Length} byte)" : $"0x{Convert.ToHexString(b)}",
        bool x => x ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}
